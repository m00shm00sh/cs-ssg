using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using LanguageExt;
using Microsoft.EntityFrameworkCore;

using CsSsg.Src.Db;
using CsSsg.Src.User;

namespace CsSsg.Src.Post;

internal static class RepositoryExtensions
{
    extension(AppDbContext ctx)
    {
        /// <summary>
        /// Gets permission level for a slug given user id.
        /// </summary>
        /// <param name="userId">user id of post accessor (null for anonymous)</param>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>Post's <see cref="AccessLevel"/> if found, otherwise <c>null</c></returns>
        public async Task<AccessLevel?> GetPermissionsForContentAsync(Guid? userId, string slug,
            CancellationToken token)
        {
            var row = await ctx.Posts
                .Where(p => p.Slug == slug && (p.AuthorId == userId || p.Public))
                .Select(p => new { p.AuthorId, p.Public })
                .SingleOrDefaultAsync(cancellationToken: token);
            if (row is null)
                return null;
            if (row.AuthorId == userId)
                return row.Public ? AccessLevel.WritePublic : AccessLevel.Write;
            if (row.Public)
                return AccessLevel.Read;
            Debug.Assert(false, "unexpected row state !public && !=uid");
            return AccessLevel.None;
        }

        /// <summary>
        /// Lists the content entries available for the given user.
        /// </summary>
        /// <param name="userId">user id of listing accessor (null for anonymous)</param>
        /// <param name="beforeOrAt">(pagination) timestamp to not query more recent than</param>
        /// <param name="limit">(pagination) maximum number of posts</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a List of <see cref="Entry"/> </returns>
        public Task<List<Entry>> GetAvailableContentAsync(Guid? userId, DateTimeOffset beforeOrAt,
            int limit, CancellationToken token)
        {
            if (userId == Guid.Empty)
                userId = null;
            return ctx.Posts.AsNoTracking()
                .Where(p => (p.AuthorId == userId || p.Public) && p.UpdatedAt < beforeOrAt)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(limit)
                .Join(ctx.Users.AsNoTracking(),
                    p => p.AuthorId,
                    u => u.Id,
                    (p, u) => new Entry
                    {
                        Slug = p.Slug,
                        Title = p.DisplayTitle,
                        AuthorHandle = u.Email,
                        IsPublic = p.Public,
                        LastModified = p.UpdatedAt,
                        AccessLevel = p.AuthorId == userId ? AccessLevel.Write : AccessLevel.Read
                    }
                ).ToListAsync(token);
        }

        /// <summary>
        /// Fetches the content. Will fail if post is inaccessible or missing.
        /// </summary>
        /// <param name="userId">user id of post accessor (null for anonymous)</param>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>the result of fetching, <see cref="Either"/> <see cref="Failure"/> or <see cref="Contents"/></returns>
        public async Task<Either<Failure, Contents>> GetContentAsync(Guid? userId, string slug, CancellationToken token)
        {
            if (userId == Guid.Empty)
                userId = null;
            var row = await ctx.Posts
                .AsNoTracking()
                .Where(p => p.Slug == slug)
                .Select(p => new
                {
                    Title = p.DisplayTitle,
                    p.Contents,
                    p.AuthorId,
                    IsPublic = p.Public
                })
                .SingleOrDefaultAsync(token);
            if (row is null)
                return Failure.NotFound;
            if (row.AuthorId != userId && !row.IsPublic)
                return Failure.NotPermitted;
            return new Contents(row.Title, row.Contents);
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
        public async Task<Either<Failure, string>> CreateContentAsync(Guid userId, Contents contents,
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
                return insertResult.ToEither(toInsert.Slug).Swap();
            toInsert.AddV7UuidToSlugForConflictResolution();
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
            return insertResult.ToEither(toInsert.Slug).Swap();
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
        /// Updates the display title and/or contents of a post. Will fail if slug not found or user isn't author.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="contents">post contents</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public async Task<Option<Failure>> UpdateContentAsync(Guid userId, string slug, Contents contents,
            CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row is null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            row.DisplayTitle = contents.Title;
            row.Contents = contents.Body;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult;
        }

        /// <summary>
        /// Renames the slug for a post. Will fail if slug not found or user isn't owner.
        /// </summary>
        /// <param name="userId">user id of post renamer</param>
        /// <param name="oldSlug">old slug name</param>
        /// <param name="newSlug">new slug name</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the result of updating with duplicate slug resolution,
        ///     <see cref="Either"/> <see cref="Failure"/> or new slug name
        /// </returns>
        public async Task<Either<Failure, string>> UpdateSlugAsync(Guid userId, string oldSlug, string newSlug,
            CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == oldSlug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            row.Slug = newSlug;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            // same retry-with-uuid logic as with CreateContentAsync, but for update
            if (updateResult.ToNullable() != Failure.Conflict)
                return updateResult.ToEither(newSlug).Swap();
            row.AddV7UuidToSlugForConflictResolution();
            updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult.ToEither(row.Slug).Swap();
        }

        /// <summary>
        /// Modifies the permissions of a post. Will fail if slug not found or user isn't author.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="permissions">the new <see cref="IManageCommand.Permissions"/> to set</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public async Task<Option<Failure>> UpdatePermissionsAsync(Guid userId, string slug,
            IManageCommand.Permissions permissions, CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            row.Public = permissions.Public;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult;
        }
        
        /// <summary>
        /// Modifies the author of a post. Will fail if slug not found or user isn't author.
        /// New author is returned on success.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="newUserEmail">email of new author</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the result of changing author,
        ///     <see cref="Either"/> <see cref="Failure"/> or new author's <see cref="Guid"/>
        /// </returns>
        public async Task<Either<Failure, Guid>> UpdateAuthorAsync(Guid userId, string slug,
            string newUserEmail, CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            
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
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult.ToEither(newUserId).Swap();
        }
        
        /// <summary>
        /// Deletes a post by slug. Will fail if slug not found or user isn't author.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public async Task<Option<Failure>> DeleteContentAsync(Guid userId, string slug, CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row is null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            ctx.Posts.Remove(row);
            await ctx.SaveChangesAsync(token);
            return Option<Failure>.None;
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
                DisplayTitle = contents.Title,
                Contents = contents.Body,
                AuthorId = authorId
            };
    }

    extension(Src.Db.Post post)
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

        internal void AddV7UuidToSlugForConflictResolution()
        {
            var uuid = Guid.CreateVersion7();
            var uuidStr = $".{uuid:N}"; // hex digits, no punctuation
            // trim slug enough to prevent DB insert string length error
            // NOTE: this is a short string; no point in complexity of spans to remove just one alloc
            post.Slug = post.Slug[..Math.Min(POST_SLUG_MAXLEN - uuidStr.Length, post.Slug.Length)] + uuidStr;
        }
    }
    
    private const int POST_SLUG_MAXLEN = 250;
    private const int POST_DISPLAYTITLE_MAXLEN = 250;
}