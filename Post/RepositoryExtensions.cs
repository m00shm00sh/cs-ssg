using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OneOf;
using OneOf.Types;

using CsSsg.Db;
using CsSsg.User;

namespace CsSsg.Post;

// we need the type reminders for OneOf<T...>.(Match|Switch)(Func<T, R>...)
[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
internal static class RepositoryExtensions
{
    extension(AppDbContext ctx)
    {
        /// Gets permission level for a slug given user id. Returns null if no such slug was found.
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
                return AccessLevel.Write;
            if (row.Public)
                return AccessLevel.Read;
            return AccessLevel.None;
        }

        /// Lists the content entries available for the given user.
        public Task<List<Entry>> GetAvailableContentAsync(Guid? userId, DateTimeOffset beforeOrAt,
            int limit, CancellationToken token)
            => ctx.Posts.AsNoTracking()
                .Where(p => (p.AuthorId == userId || p.Public) && p.UpdatedAt < beforeOrAt)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(limit)
                .Select(p => new Entry
                {
                    Slug = p.Slug,
                    Title = p.DisplayTitle,
                    LastModified = p.UpdatedAt,
                    AccessLevel = p.AuthorId == userId ? AccessLevel.Write : AccessLevel.Read
                }).ToListAsync(token);

        // Fetches the content. Will fail if post is inaccessible or missing.
        public async Task<OneOf<Contents, Failure>> GetContentAsync(Guid? userId, string slug, CancellationToken token)
        {
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

        /// Creates a new blog post in the database. On slug name conflict, a second attempt is made by appending
        /// a UUID to the slug name and retrying. Constraint failure errors are propagated in the return value.
        public async Task<OneOf<string, Failure>> CreateContentAsync(Guid userId, Contents contents,
            CancellationToken token)
        {
            var toInsert = contents.ToDbRow(userId);
            var validity = toInsert.CheckValidity();
            if (validity is not null)
                return validity.Value;
            var insertResult = await ctx.TryToInsertContentAsync(toInsert, rollbackOnFailure: true,
                token: token);
            var retryWithUuid = insertResult.Match(
                (string _) => false,
                (Failure failure) =>
                    failure switch
                    {
                        Failure.Conflict => true,
                        _ => false,
                    }
            );
            if (!retryWithUuid)
                return insertResult;
            toInsert.AddV7UuidToTitleForConflictResolution();
            insertResult = await ctx.TryToInsertContentAsync(toInsert, token);
            insertResult.Switch(
                /* (string _) => _ */ null,
                (Failure f) =>
                {
                    var exceptionMessage = f switch
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
            return insertResult;
        }

        /// Tries to insert a Post (with cancellation) and roll back the entity tracking on failure if desired.
        private async Task<OneOf<string, Failure>> TryToInsertContentAsync(Db.Post post, CancellationToken token,
            bool rollbackOnFailure = false)
        {
            var rowMeta = await ctx.Posts.AddAsync(post, token);
            var result = await ctx.TryToCommitChangesAsync(token);
            if (result is null)
                return post.Slug;
            // if desired, roll back on failure so that the next call to DbContext.SaveChangesAsync doesn't try to
            // insert the failing value again
            if (rollbackOnFailure)
                rowMeta.State = EntityState.Detached;
            return result.Value;
        }

        /// Updates the display title and/or contents of a post. Will fail if slug not found or user isn't author.
        public async Task<Failure?> UpdateContentAsync(Guid userId, string slug, Contents contents,
            CancellationToken token)
            => (await ctx.UpdateContentIfOlderThanAsync(userId, slug, contents, token, olderThan: null)).Match(
                (bool _) => (Failure?)null,
                (Failure f) => f
            );
        
        /// Updates the display title and/or contents of a post. Will fail if slug not found or user isn't author.
        /// Will return false if olderThan is not null and the condition isn't met.
        public async Task<OneOf<bool, Failure>> UpdateContentIfOlderThanAsync(Guid userId, string slug,
            Contents contents, CancellationToken token, DateTime? olderThan = null)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row is null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            if (olderThan.HasValue && row.UpdatedAt > olderThan.Value)
                return false;
            row.DisplayTitle = contents.Title;
            row.Contents = contents.Body;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult is null ? true : updateResult.Value;
        }

        /// Renames the slug for a post. Will fail if slug not found or user isn't owner.
        public async Task<OneOf<Success, Failure>> UpdateSlugAsync(Guid userId, string oldSlug, string newSlug,
            CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == oldSlug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            row.Slug = newSlug;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult is null ? new Success() : updateResult.Value;
        }

        /// Modifies the public state of a post. Will fail if slug not found or user isn't author.
        public async Task<OneOf<Success, Failure>> UpdatePermissionsAsync(Guid userId, string slug,
            bool newPublic, CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            row.Public = newPublic;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult is null ? new Success() : updateResult.Value;
        }

        /// Modifies the author of a post. Will fail if slug not found or user isn't author.
        public async Task<OneOf<Success, Failure>> SetAuthorAsync(Guid userId, string slug,
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
            findUserResult.Switch(
                (Guid id) => newUserId = id,
                (Failure f) => failCode = f
            );
            if (newUserId != Guid.Empty)
                return failCode;
            
            row.AuthorId = newUserId;
            var updateResult = await ctx.TryToCommitChangesAsync(token);
            return updateResult is null ? new Success() : updateResult.Value;
        }
        
        /// Deletes a post by slug. Will fail if slug not found or user isn't author.
        public async Task<OneOf<Success, Failure>> DeleteContentAsync(Guid userId, string slug, CancellationToken token)
        {
            var row = await ctx.Posts.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row is null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            ctx.Posts.Remove(row);
            await ctx.SaveChangesAsync(token);
            return new Success();
        }
    }
}

file static class RepositoryExtensionsHelpers
{
    extension(Contents contents)
    {
        internal Db.Post ToDbRow(Guid authorId)
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
            if (post.DisplayTitle.Length > POST_DISPLAYTITLE_MAXLEN)
                return Failure.TooLong;
            if (post.Slug.Length > POST_SLUG_MAXLEN)
                throw new InvalidOperationException(
                    "Slug name is computed from DisplayTitle and it ended up being too long.");
            return null;
        }

        internal void AddV7UuidToTitleForConflictResolution()
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