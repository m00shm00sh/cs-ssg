using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using CsSsg.Src.Auth;
using LanguageExt;
using Microsoft.EntityFrameworkCore;

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
    
    internal record PostPermissions(Guid AuthorId, List<string> Tags, ConcurrencyToken ConcurrencyToken);
    
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
        /// Lists the content entries available for the given user.
        /// </summary>
        /// <param name="user">user identity of listing accessor (null for anonymous)</param>
        /// <param name="flags">fetch filter (see <see cref="ListingFilter"/>)</param>
        /// <param name="beforeOrAt">(pagination) timestamp to not query more recent than</param>
        /// <param name="limit">(pagination) maximum number of posts</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a List of <see cref="Entry"/> </returns>
        public Task<List<Entry>> GetAvailableContentAsync(ClaimsPrincipal? user, ListingFilter flags, 
            DateTimeOffset beforeOrAt, int limit, CancellationToken token)
        {
            if (user == null)
                user = AuthenticationExtensions.NullUser;
            var userId = user.TryGetUid();
            if (userId is null)
                flags |= ListingFilter.Tags;
            var userOnly = (flags & ListingFilter.UserOnly) == ListingFilter.UserOnly;
            var tagsOnly = (flags & ListingFilter.Tags) == ListingFilter.Tags;
            // no anonymous posts so this will be an empty list
            if (userId == null && userOnly)
                return Task.FromResult(new List<Entry>());

            var searchGroups = user.GetRoles(RoleNamespace.Search).ToList();
            var writeGroups = user.GetRoles(RoleNamespace.Edit).ToList();
            
            var postQuery = ctx.Posts.AsNoTracking()
                .Where(p => p.UpdatedAt < beforeOrAt);

            var userQuery = postQuery
                .Where(p => p.AuthorId == userId)
                .Include(p => p.Tags);
            var publicQuery = postQuery
                .Include(p => p.Tags
                    .Where(t => searchGroups.Contains(t.Tag)))
                .Where(p => p.Tags.Count > 0);

            if (userOnly)
                postQuery = tagsOnly ? userQuery.Intersect(publicQuery) : userQuery;
            else
                postQuery = tagsOnly ? publicQuery : userQuery.Union(publicQuery);
            
            postQuery = postQuery
                .OrderByDescending(e => e.UpdatedAt)
                .Take(limit);
            // split the query at the join point so type inference doesn't get confused about entity type    
            var query = postQuery.Include(p => p.Author)
                .Select(p => new Entry
                    {
                        Slug = p.Slug,
                        Title = p.DisplayTitle,
                        AuthorHandle = p.Author.Email,
                        AccessLevel = p.AuthorId == userId
                            ? AccessLevel.FullControl
                            : writeGroups.Intersect(p.Tags.Select(t => t.Tag)).Any()
                                ? AccessLevel.Write
                                : AccessLevel.Read,
                        Tags = p.Tags.Select(t => t.Tag).ToList(),
                        LastModified = p.UpdatedAt,
                });
            return query.ToListAsync(token);
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
                .Select(p => new
                {
                    Title = p.DisplayTitle,
                    p.Contents,
                    ModifyTime = p.UpdatedAt,
                    CToken = p.PVer
                })
                .SingleOrDefaultAsync(token);
            if (row is null)
                return Failure.NotFound;
            if (row.CToken != cToken.Value)
                return Failure.Conflict;

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
            var insertResult = await ctx.TryToInsertContentAsync(toInsert, rollbackOnFailure: true,
                token: token);
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
                return insertResult.ToEither(new InsertResult(toInsert.Slug, false)).Swap();
           
            RepositoryExtensionsHelpers.AddV7UuidToSlugForConflictResolution(toInsert);
            insertResult = await ctx.TryToInsertContentAsync(toInsert, token);
            insertResult.IfSome(
                failCode =>
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
            return insertResult.ToEither(new InsertResult(toInsert.Slug, true)).Swap();
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
            var rowMeta = await ctx.Posts.AddAsync(post, token);
            var result = await ctx.TryToCommitChangesAsync(token);
            result.IfSome(_ =>
            {
                // if desired, roll back on failure so that the next call to DbContext.SaveChangesAsync doesn't try
                // to insert the failing value again
                if (rollbackOnFailure)
                    rowMeta.State = EntityState.Detached;
            });
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
        public async Task<Option<Failure>> UpdateContentAsync(Guid userId, string slug, Contents contents,
            ConcurrencyToken cToken, CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row is null)
                return Failure.NotFound;
            if (row.PVer != cToken.Value)
                return Failure.Conflict;
            row.DisplayTitle = contents.Title;
            row.Contents = contents.Body;
            row.AuthorId = userId;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult;
        }

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
        ///     Modifies the permissions of a post.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="permissions">the new <see cref="IManageCommand.Permissions"/> to set</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        // TODO: we should use some dummy column to track permission changes so that mtime is consistent 
        public Task<Option<Failure>> UpdatePermissionsAsync(Guid userId, string slug,
            IManageCommand.Permissions permissions, ConcurrencyToken cToken, CancellationToken token)
            => ctx.DoUpdatePermissionsAsync<Db.Post, PostTag>(
                ctx.Posts, userId, slug, permissions, cToken, token);
        
        internal async Task<Option<Failure>> DoUpdatePermissionsAsync<TTable, TTag>(
            DbSet<TTable> table, Guid userId, string slug, IManageCommand.Permissions permissions,
            ConcurrencyToken cToken, CancellationToken token)
            where TTable : class, IHasAuthorAndSlug, IHasTag<TTag>
            where TTag : ITag, new()
        {
            IEnumerable<string> groups = [TAG_PUBLIC];

            var row = await table
                .Include(post => post.Tags
                    .Where(prg => groups.Contains(prg.Tag))
                ).SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.PVer !=  cToken.Value)
                return Failure.Conflict;
            
            // copy to list to clean up logic (we must still call Remove on the navigation object)
            var tags = row.Tags.ToList();
            var seenPublic = tags.FirstOrDefault(t => t.Tag == TAG_PUBLIC);
            
            if (permissions.Public)
            {
                if (seenPublic is null)
                    row.Tags.Add(new TTag()
                    {
                        Tag = TAG_PUBLIC
                    });
            }
            else
            {
                if (seenPublic is not null)
                    row.Tags.Remove(seenPublic);
            }

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
        
        internal async Task<Option<Failure>> DoDeleteContentAsync<TTable>(DbSet<TTable> table, Guid userId, string slug,
            ConcurrencyToken cToken, CancellationToken token)
            where TTable : class, IHasPermissionsVersion, IHasAuthorAndSlug
        {
            var row = await table.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row is null)
                return Failure.NotFound;
            if (row.PVer !=  cToken.Value)
                return Failure.Conflict;
            table.Remove(row);
            await ctx.SaveChangesAsync(token);
            return Option<Failure>.None;
        }
    }

    internal record struct InsertResult(string InsertedName, bool DidDuplicateResolution);
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
                DisplayTitle = contents.Title,
                Contents = contents.Body,
                AuthorId = authorId
            };
    }

    extension(Db.Post post)
    {
        internal Failure? CheckValidity()
        {
            if (string.IsNullOrEmpty(post.Slug))
                return Failure.Conflict;
            if (post.DisplayTitle.Length > POST_DISPLAYTITLE_MAXLEN)
                return Failure.TooLong;
            if (post.Slug.Length > POST_SLUG_MAXLEN)
                throw new InvalidOperationException(
                    "Slug name is computed from DisplayTitle and it ended up being too long.");
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
