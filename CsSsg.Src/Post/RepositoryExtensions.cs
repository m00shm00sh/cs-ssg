using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using EntityFrameworkCore.Locking;
using LanguageExt;
using Microsoft.EntityFrameworkCore;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.User;

namespace CsSsg.Src.Post;

internal static class RepositoryExtensions
{
    /// <summary>
    /// Filtering flags for listing fetch to facilitate restricting to user and/or public
    /// </summary>
    [Flags]
    internal enum ListingFilter
    {
        /// only fetch user's posts
        UserOnly = 1 << 0,
        /// only fetch public posts
        Tags = 1 << 1,
    }
    internal const string TAG_PUBLIC = "public";
    internal const string TAG_UNLISTED = "unlisted";

    internal readonly record struct ConcurrencyToken(int Value)
    {
        // we need the charade of alternate constructor for it to set the field default value as desired
        public ConcurrencyToken()
        : this(1) 
        {}
        
        internal ConcurrencyToken Next()
            => new(Value + 1);
    }
    
    internal record PostPermissions(Guid AuthorId, IReadOnlyCollection<string> Tags, ConcurrencyToken ConcurrencyToken);
    
    extension(AppDbContext ctx)
    {
        
        /// <summary>
        /// Gets permissions (author and tags) for a slug given user id.
        /// </summary>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>Post's <see cref="PostPermissions"/> if found, otherwise <c>null</c></returns>
        public Task<PostPermissions?> GetPermissionsForContentAsync(string slug, CancellationToken token)
            => ctx.Posts.AsNoTracking()
                .Where(p => p.Slug == slug)
                .Include(p => p.Tags)
                .Select(p => new PostPermissions(
                    p.AuthorId,
                    p.Tags.Select(t => t.Tag).ToList(),
                    new ConcurrencyToken(p.PVer)
                ))
                .SingleOrDefaultAsync(token);

        /// <summary>
        /// Gets revisions for content.
        /// </summary>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="cToken">permissions concurrency token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns></returns>
        public async Task<Either<Failure, List<Revision>>> GetRevisionsForContentAsync(
            string slug, ConcurrencyToken cToken, CancellationToken token)
        {
            var meta = await ctx.Posts.AsNoTracking()
                .Where(p => p.Slug == slug)
                .Include(p => p.Revisions)
                .Select(p => new
                    {
                        Revisions = p.Revisions.Select(r => new Revision
                            {
                                Id = r.Id,
                                Title = r.DisplayTitle,
                                ContentLength = r.Contents.Length,
                                AuthorHandle = r.Author != null ? r.Author.Email : null!,
                                Created = r.CreatedAt
                            }).ToList(),
                        p.PVer
                    }).SingleOrDefaultAsync(token);
            if (meta == null)
                return Failure.NotFound;
            if (meta.PVer != cToken.Value)
                return Failure.Conflict;
            return meta.Revisions;
        }

        /// <summary>
        /// Lists the content entries available for the given user.
        /// </summary>
        /// <param name="user">user identity of listing accessor (null for anonymous)</param>
        /// <param name="flags">fetch filter (see <see cref="ListingFilter"/>)</param>
        /// <param name="filterTags">secondary filtering tags</param>
        /// <param name="beforeOrAt">(pagination) timestamp to not query more recent than</param>
        /// <param name="limit">(pagination) maximum number of posts</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a List of <see cref="Entry"/> </returns>
        public async Task<List<Entry>> GetAvailableContentAsync(ClaimsPrincipal? user, ListingFilter flags, 
            ICollection<string> filterTags, DateTimeOffset beforeOrAt, int limit, CancellationToken token)
        {
            if (!user.TryGetUidAndSave(out var userId))
            {
                user = AuthenticationExtensions.NullUser;
                userId = Guid.Empty;
            }
            if (userId == Guid.Empty)
                flags |= ListingFilter.Tags;
            var userOnly = (flags & ListingFilter.UserOnly) == ListingFilter.UserOnly;
            var tagsOnly = (flags & ListingFilter.Tags) == ListingFilter.Tags;
            // no anonymous posts so this will be an empty list
            if (userId == Guid.Empty && userOnly)
                return [];

            IReadOnlyList<Revision> emptyRevisions = [];

            var searchGroups = user.GetRoles(RoleNamespace.Search).ToList();
            var writeGroups = user.GetRoles(RoleNamespace.Edit).ToList();

            var tagsTable = ctx.PostTags.AsNoTracking();
            var postsTable = ctx.Posts.AsNoTracking();

            IQueryable<Db.Post> postsQuery;

            if (tagsOnly || filterTags.Count == 0)
            {
                var findPostsByTagQuery = tagsTable
                    .Where(t => searchGroups.Contains(t.Tag))
                    .Select(t => t.PostId);

                if (filterTags.Count > 0)
                    findPostsByTagQuery = findPostsByTagQuery
                        .Intersect(tagsTable
                            .Where(t => filterTags.Contains(t.Tag))
                            .Select(t => t.PostId));
                
                if (userOnly)
                    postsQuery = postsTable
                        .Where(p =>
                            p.AuthorId == userId
                            && (!tagsOnly || findPostsByTagQuery.Contains(p.Id))
                        );
                else if (filterTags.Count == 0)
                    postsQuery = postsTable
                        .Where(p =>
                            p.AuthorId == userId
                            || findPostsByTagQuery.Contains(p.Id)
                        );
                else
                    postsQuery = postsTable
                        .Where(p => findPostsByTagQuery.Contains(p.Id));
            }
            else // case (!tags && filter) means the filter intersects the "traditional" query
            {
                var findPostsByUserTagQuery = tagsTable
                    .Where(t => searchGroups.Contains(t.Tag))
                    .Select(t => t.PostId);
                var findPostsByFilterTagQuery = tagsTable
                    .Where(t => filterTags.Contains(t.Tag))
                    .Select(t => t.PostId);
                postsQuery = postsTable
                    .Where(p => 
                        (p.AuthorId == userId || findPostsByUserTagQuery.Contains(p.Id))
                        && findPostsByFilterTagQuery.Contains(p.Id)
                    );
            }

            postsQuery = postsQuery
                .Include(p => p.Author)
                .Include(p => p.Tags)
                .Include(p => p.LatestRevision)
                .Include(p => p.Revisions)
                .Where(p => p.UpdatedAt < beforeOrAt)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(limit);
            
            var query = postsQuery
                .Select(p => new Entry
                    {
                        Slug = p.Slug,
                        LatestTitle = p.LatestRevision != null ? p.LatestRevision.DisplayTitle : null!,
                        AuthorHandle = p.Author.Email,
                        AccessLevel = p.AuthorId == userId
                            ? AccessLevel.FullControl
                            : writeGroups.Intersect(p.Tags.Select(t => t.Tag)).Any()
                                ? AccessLevel.Write
                                : AccessLevel.Read,
                        Tags = p.Tags.Select(t => t.Tag).ToList(),
                        RevisionCount = p.Revisions.Count,
                        Revisions = emptyRevisions,
                        LastModified = p.UpdatedAt,
                });
            var result = await query.ToListAsync(token);
            foreach (var entry in result)
            {
                if (entry.LatestTitle == null)
                    throw new InvalidOperationException($"for slug {entry.Slug} we have a null LatestRevision");
            }
            return result;
        }

        /// <summary>
        /// Queries modify timestamp for slug.
        /// </summary>
        /// <param name="slug">slug name</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>an Optional of <see cref="DateTimeOffset"/> or <c>None</c></returns>
        public async Task<Option<DateTimeOffset>> GetModifyTimeAsync(string slug, CancellationToken token)
        {
            var row = await ctx.Posts.AsNoTracking()
                .Where(p => p.Slug == slug)
                .Select(p => p.UpdatedAt)
                .SingleOrDefaultAsync(token);
            return row != default ? (DateTimeOffset)row.ToUniversalTime() : Option<DateTimeOffset>.None;
        }

        /// <summary>
        /// Fetches the content. Will fail if post is missing.
        /// </summary>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>the result of fetching, <see cref="Either"/> <see cref="Failure"/> or <see cref="Contents"/></returns>
        public async Task<Either<Failure, Contents>> GetContentAsync(string slug, ConcurrencyToken cToken,
            CancellationToken token)
        {
            var row = await ctx.Posts
                .AsNoTracking()
                .Where(p => p.Slug == slug)
                .Include(p => p.LatestRevision)
                .Select(p => new
                {
                    Title = p.LatestRevision != null ? p.LatestRevision.DisplayTitle : null,
                    Contents = p.LatestRevision != null ? p.LatestRevision.Contents : null,
                    ModifyTime = p.UpdatedAt,
                    CToken = p.PVer
                })
                .SingleOrDefaultAsync(token);
            if (row is null)
                return Failure.NotFound;
            if (row.CToken != cToken.Value)
                return Failure.Conflict;
            
            if (row.Title == null || row.Contents == null)
                throw new InvalidOperationException($"for slug {slug} we have a null LatestRevision");

            return new Contents(row.Title, row.Contents, row.ModifyTime);
        }

        /// <summary>
        /// Creates a new blog post in the database. On slug name conflict, a second attempt is made by appending
        /// a UUID to the slug name and retrying. Constraint failure errors are propagated in the return value.
        /// </summary>
        /// <param name="userId">user id of author</param>
        /// <param name="contents">post contents</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>the result of inserting, <see cref="Either"/> <see cref="Failure"/> or inserted slug name</returns>
        /// <exception cref="InvalidOperationException">if an internal error occurs during duplicate handling</exception>
        public async Task<Either<Failure, InsertResult>> CreateContentAsync(Guid userId, Contents contents,
            CancellationToken token)
        {
            var toInsert = contents.ToDbRow(userId);
            var validity = toInsert.CheckValidity();
            if (validity is not null)
                return validity.Value;

            var didResolveDuplicate = false;
            
            var insertResult = await ctx.ExecuteFailableTransactionAsync(async _ =>
            {
                didResolveDuplicate = await DoCreateSlugInsideTransactionAsync(ctx.Posts, userId, toInsert,
                    ctx.TryToInsertContentAsync, RepositoryExtensionsHelpers.AddV7UuidToSlugForConflictResolution,
                    token);
            }, token);

            return insertResult.ToEither(new InsertResult(toInsert.Slug, didResolveDuplicate)).Swap();
        }

        /// <summary>
        /// Tries to insert a Post (with cancellation) and roll back the entity tracking on failure if desired.
        /// </summary>
        /// <param name="post">the post to insert</param>
        /// <param name="token">async cancellation token</param>
        /// <param name="rollbackOnFailure">if true, simulate a rollback on failure by discarding the attempt</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        private async Task<Option<Failure>> TryToInsertContentAsync(Src.Db.Post post, CancellationToken token,
            bool rollbackOnFailure = false)
        {
            if (post.Revisions.Count != 1)
                throw new InvalidOperationException("an initial revision must be supplied");
            // NOTE: we need two save phases because creation has two phases:
            //       1. insert post and revision (which would be an insert returning inside insert to grab post id)
            //       2. update post's last revision key
            var rowMeta = await ctx.Posts.AddAsync(post, token);
            var result = await ctx.TryToCommitChangesAsync(token);
            if (result.Case != null)
            {
                // if desired, roll back on failure so that the next call to DbContext.SaveChangesAsync doesn't try
                // to insert the failing value again
                if (rollbackOnFailure)
                    rowMeta.State = EntityState.Detached;
                return result;
            }
            
            // we need a second query to establish the latest revision id without dropping from EF to SQL
            post.LatestRevision = post.Revisions.Last();
            await ctx.TryToCommitChangesAsync(token);
            return result;
        }

        /// <summary>
        ///     Updates the display title and/or contents of a post.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="contents">post contents</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public Task<Option<Failure>> UpdateContentAsync(Guid userId, string slug, Contents contents,
            ConcurrencyToken cToken, CancellationToken token)
            => ctx.ExecuteFailableTransactionAsync(async _ =>
            {

                var postRow = await ctx.Posts.Where(p => p.Slug == slug)
                    .ForUpdate()
                    .SingleOrDefaultAsync(token);
                if (postRow is null)
                    throw new FailureException(Failure.NotFound);
                if (postRow.PVer != cToken.Value)
                    throw new FailureException(Failure.Conflict);

                var revRow = new PostRevision
                {
                    DisplayTitle = contents.Title,
                    Contents = contents.Body,
                    AuthorId = userId,
                };

                postRow.Revisions.Add(revRow);
                await ctx.SaveChangesAsync(token);
                
                postRow.LatestRevision = revRow;
                await ctx.SaveChangesAsync(token);
            }, token);

        /// <summary>
        ///     Renames the slug for a post.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id of post renamer</param>
        /// <param name="oldSlug">old slug name</param>
        /// <param name="newSlug">new slug name</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the result of updating with duplicate slug resolution,
        ///     <see cref="Either"/> <see cref="Failure"/> or new slug name
        /// </returns>
        public Task<Either<Failure, string>> UpdateSlugAsync(Guid userId, string oldSlug, string newSlug,
            ConcurrencyToken cToken, CancellationToken token)
            =>  ctx.DoUpdateSlugAsync(ctx.Posts, userId, oldSlug, newSlug, 
                    RepositoryExtensionsHelpers.AddV7UuidToSlugForConflictResolution, cToken, token);
        
        internal async Task<Either<Failure, string>> DoUpdateSlugAsync<TTable>(
            DbSet<TTable> table, Guid userId, string oldSlug, string newSlug, Action<TTable> conflictRenamer, 
            ConcurrencyToken cToken, CancellationToken token)
            where TTable : class, IHasAuthorAndSlug, IHasPermissionsVersion
        {
            var row = await table.SingleOrDefaultAsync(p => p.Slug == oldSlug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.PVer != cToken.Value)
                return Failure.Conflict;
            row.Slug = newSlug;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            // same retry-with-uuid logic as with CreateContentAsync, but for update
            if (updateResult.ToNullable() != Failure.Conflict)
                return updateResult.ToEither(newSlug).Swap();
            conflictRenamer(row);
            updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult.ToEither(row.Slug).Swap();
        }

        /// <summary>
        ///     Modifies the tags of a post.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="tags">the new <see cref="IManageCommand.PostTags"/> to set</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public Task<Option<Failure>> UpdatePermissionsAsync(Guid userId, string slug,
            IManageCommand.PostTags tags, ConcurrencyToken cToken, CancellationToken token)
            => ctx.DoUpdateTagsAsync<Db.Post, PostTag>(
                ctx.Posts, userId, slug, tags, cToken, token);
        
        // TODO: differential tagging
        internal async Task<Option<Failure>> DoUpdateTagsAsync<TTable, TTag>(
            DbSet<TTable> table, Guid userId, string slug, IManageCommand.PostTags newTagsP,
            ConcurrencyToken cToken, CancellationToken token)
            where TTable : class, IHasAuthorAndSlug, IHasTag<TTag>
            where TTag : ITag, new()
        {
            IEnumerable<string> groups = newTagsP.LowerToStringList();

            var row = await table
                .Include(post => post.Tags
                    // TODO: uncomment subsequent line when differential tagging is implemented
                    // .Where(prg => groups.Contains(prg.Tag))
                )
                .SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.PVer !=  cToken.Value)
                return Failure.Conflict;
            
            // copy to list to clean up logic (we must still call Remove on the navigation object)
            var tags = row.Tags.ToList();

            var toDelete = tags.ExceptBy(groups, t => t.Tag).ToList();
            var toAdd = groups.Except(tags.Select(t => t.Tag)).ToList();

            if (toDelete.Count == 0 && toAdd.Count == 0)
                return Option<Failure>.None;

            foreach (var tag in toAdd)
                row.Tags.Add(new TTag
                {
                    Tag = tag
                });
            foreach (var tag in toDelete)
                row.Tags.Remove(tag);

            row.PVer = cToken.Value + 1;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult;
        }
        
        /// <summary>
        ///     Modifies the author of a post.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        ///     New author is returned on success.
        /// </summary>
        /// <param name="userId">user id of author updater</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="newUserEmail">email of new author</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the result of changing author,
        ///     <see cref="Either"/> <see cref="Failure"/> or new author's <see cref="Guid"/>
        /// </returns>
        public Task<Either<Failure, Guid>> UpdateAuthorAsync(Guid userId, string slug,
            string newUserEmail, ConcurrencyToken cToken, CancellationToken token)
            => ctx.DoUpdateAuthorAsync(ctx.Posts, userId, slug, newUserEmail, cToken, token);
        
        internal async Task<Either<Failure, Guid>> DoUpdateAuthorAsync<TTable>(
            DbSet<TTable> table, Guid userId, string slug, string newUserEmail, ConcurrencyToken cToken, 
            CancellationToken token)
            where TTable : class, IHasAuthorAndSlug, IHasPermissionsVersion
        {
            var row = await table.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.PVer !=  cToken.Value)
                return Failure.Conflict;
            
            var newUserId = Guid.Empty;
            var findUserResult = await ctx.FindUserByEmailAsync(newUserEmail, token);
            var failCode = default(Failure);
            findUserResult.Match(
                id => newUserId = id,
                f => failCode = f
            );
            if (newUserId == Guid.Empty)
                return failCode;
            
            row.AuthorId = newUserId;
            // change of author changes the permissions state so update the permissions version counter here too
            row.PVer = cToken.Value + 1;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult.ToEither(newUserId).Swap();
        }
        
        /// <summary>
        ///     Deletes a post by slug.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public Task<Option<Failure>> DeleteContentAsync(Guid userId, string slug, ConcurrencyToken cToken,
            CancellationToken token)
            => ctx.DoDeleteContentAsync(ctx.Posts, userId, slug, cToken, token);
        
        internal Task<Option<Failure>> DoDeleteContentAsync<TTable>(DbSet<TTable> table, Guid userId,
            string slug, ConcurrencyToken cToken, CancellationToken token)
            where TTable : class, IIdTable, IHasPermissionsVersion, IHasAuthorAndSlug
        {
            var tableName = table.EntityType.GetTableName() 
                ?? throw new InvalidOperationException($"Table {table.EntityType} has no table name");

            return ctx.ExecuteFailableTransactionAsync(async _ =>
            {
                // fetch the row and optimistic concurrency tokens only; we will use raw sql for the delete because
                // EF's change tracker doesn't like the weak circular reference and this will avoid an unnecessary
                // UPDATE whose only role is to set a FK ID to NULL to kill a dependency
                var row = await table
                    .AsNoTracking()
                    .Where(p => p.Slug == slug)
                    .ForUpdate()
                    .Select(p => new { p.Id, p.PVer })
                    .SingleOrDefaultAsync(token);

                if (row is null)
                    throw new FailureException(Failure.NotFound);
                if (row.PVer != cToken.Value)
                    throw new FailureException(Failure.Conflict);

                // postgres does not allow us to parameterize the table to delete from so we have to use raw sql here
            #pragma warning disable EF1002
                await ctx.Database.ExecuteSqlRawAsync($"DELETE FROM {tableName} WHERE id='{row.Id}'", token);
            #pragma warning restore EF1002
            }, token);
        }
    }
    
    internal static async Task<bool> DoCreateSlugInsideTransactionAsync<TTable>(
        DbSet<TTable> table, Guid authorId, TTable toInsert, 
        Func<TTable, CancellationToken, bool, Task<Option<Failure>>> attemptInsert, Action<TTable> conflictRenamer,
        CancellationToken token)
        where TTable : class, IHasAuthorAndSlug
    {
        var insertResult = await attemptInsert(toInsert, token, /* rollBackOnFailure */ true);
        var retryWithUuid = insertResult.Match(
            failCode =>
                failCode switch
                {
                    Failure.Conflict => true,
                    _ => false
                },
            () => false
        );

        if (!retryWithUuid)
        {
            insertResult.IfSome(f => throw new FailureException(f));
            return false;
        }
            
        conflictRenamer(toInsert);
        insertResult = await attemptInsert(toInsert, token, /* rollBackOnFailure */ false);
        insertResult.IfSome(failCode =>
            {
                var exceptionMessage = failCode switch
                {
                    Failure.Conflict =>
                        "We have a UNIQUE conflict after appending a V7 UUID. This should not happen.",
                    Failure.TooLong =>
                        "We have a string length conflict after appending a UUID. This should not happen.",
                    _ => null
                };
                if (exceptionMessage != null)
                    throw new InvalidOperationException(exceptionMessage);
            }
        );
        return true;
    }

    internal record struct InsertResult(string InsertedName, bool DidDuplicateResolution);
}

internal static class RepositoryExtensionsSharedHelpers
{
    internal static IManageCommand.PostTags StringListToTags(IReadOnlyCollection<string> tags)
    {
        var visibility = IManageCommand.PostVisibility.Tags;
        if (tags.Contains(RepositoryExtensions.TAG_UNLISTED))
            visibility = IManageCommand.PostVisibility.Unlisted;
        if (tags.Contains(RepositoryExtensions.TAG_PUBLIC))
            visibility = IManageCommand.PostVisibility.Public;
        var otherTags = tags.Except([RepositoryExtensions.TAG_PUBLIC, RepositoryExtensions.TAG_UNLISTED]);
        return new IManageCommand.PostTags(visibility, otherTags);
    }
    
    extension(IManageCommand.PostTags pTags)
    { 
        internal List<string> LowerToStringList()
        {
            var l = new List<string>();
            switch (pTags.Visibility)
            {
                case IManageCommand.PostVisibility.Public:
                    l.Add("public");
                    break;
                case IManageCommand.PostVisibility.Unlisted:
                    l.Add("unlisted");
                    break;
            }
            l.AddRange(pTags.Tags.Select(s => s.ToLower()));
            return l;
        }
    }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
file static class RepositoryExtensionsHelpers
{
    extension(Contents contents)
    {
        internal Src.Db.Post ToDbRow(Guid authorId)
            => new()
            {
                Slug = contents.ComputeSlugName(),
                AuthorId = authorId,
                Revisions = [
                    new PostRevision
                    {
                        DisplayTitle = contents.Title,
                        Contents = contents.Body,
                        AuthorId = authorId
                    }
                ]
            };
    }


    extension(Src.Db.Post post)
    {
        internal Failure? CheckValidity()
        {
            if (string.IsNullOrEmpty(post.Slug))
                return Failure.Conflict;
            if (post.Revisions.LastOrDefault()?.DisplayTitle.Length > POST_DISPLAYTITLE_MAXLEN)
                return Failure.TooLong;
            if (post.Slug.Length > POST_SLUG_MAXLEN)
                throw new InvalidOperationException(
                    "Slug name is computed from DisplayTitle and it ended up being too long.");
            return null;
        }
    }

    extension(PostRevision revision)
    {
        internal Failure? CheckValidity()
        {
            if (revision.DisplayTitle.Length > POST_DISPLAYTITLE_MAXLEN)
                return Failure.TooLong;
            return null;
        }
        
    }
    
    internal static void AddV7UuidToSlugForConflictResolution(Db.Post post)
    {
        var uuid = Guid.CreateVersion7();
        var uuidStr = $".{uuid:N}"; // hex digits, no punctuation
        // trim slug enough to prevent DB insert string length error
        // NOTE: this is a short string; no point in complexity of spans to remove just one alloc
        post.Slug = post.Slug[..Math.Min(POST_SLUG_MAXLEN - uuidStr.Length, post.Slug.Length)] + uuidStr;
    }
    
    private const int POST_SLUG_MAXLEN = 250;
    private const int POST_DISPLAYTITLE_MAXLEN = 250;
}
