using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using static CsSsg.Src.Post.IManageCommand;
using static CsSsg.Src.Post.RepositoryExtensions;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.User;

namespace CsSsg.Src.Media;

internal static class RepositoryExtensions
{
    extension(AppDbContext ctx)
    {
        /// <summary>
        /// Gets metadata for a slug given user id.
        /// </summary>
        /// <param name="userId">user id of post accessor (null for anonymous)</param>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>Media's <see cref="AccessLevel"/> if found, otherwise <c>null</c></returns>
        public async Task<Entry?> GetMetadataForMediaAsync(Guid? userId, string slug,
            CancellationToken token)
        {
            var row = await ctx.Media
                .Where(m => m.Slug == slug && (m.AuthorId == userId || m.Public))
                .Select(m => new
                {
                    m.AuthorId,
                    m.ContentType, 
                    Size = m.ContentLength,
                    m.Public
                })
                .SingleOrDefaultAsync(cancellationToken: token);
            if (row is null)
                return null;
            var entry = new Entry
            {
                ContentType = row.ContentType,
                Size = row.Size,
                AccessLevel = row.Public ? AccessLevel.Read : AccessLevel.None
            };
            if (row.AuthorId == userId)
                entry = entry with { AccessLevel = row.Public ? AccessLevel.WritePublic : AccessLevel.Write };
            return entry;
        }

        /// <summary>
        /// Lists the content entries owned by the given user.
        /// </summary>
        /// <param name="userId">user id of listing accessor</param>
        /// <param name="beforeOrAt">(pagination) timestamp to not query more recent than</param>
        /// <param name="limit">(pagination) maximum number of posts</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a List of <see cref="Entry"/> </returns>
        public async Task<List<Entry>> GetAllMediaForOwnerAsync(Guid userId, DateTimeOffset beforeOrAt,
            int limit, CancellationToken token)
        {
            var entries = await ctx.Media.AsNoTracking()
                .Where(m => (m.AuthorId == userId || m.Public) && m.UpdatedAt < beforeOrAt)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(limit)
                .Select(m => new Entry
                    {
                        Slug = m.Slug,
                        Size = m.ContentLength,
                        IsPublic = m.Public,
                        LastModified = m.UpdatedAt,
                        AccessLevel = m.AuthorId == userId ? AccessLevel.Write : AccessLevel.Read
                    }
                ).ToListAsync(token);
            return entries;
        }

        /// <summary>
        /// Fetches content data. Will fail if post is inaccessible or missing.
        /// </summary>
        /// <param name="userId">user id of post accessor (null for anonymous)</param>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>the result of fetching, <see cref="Either"/> <see cref="Failure"/> or the object</returns>
        public async Task<Either<Failure, Object>> GetObjectForSlug(Guid? userId, string slug, CancellationToken token)
        {
            if (userId == Guid.Empty)
                userId = null;
            var row = await ctx.Media
                .AsNoTracking()
                .Where(m => m.Slug == slug)
                .Select(m => new
                {
                    m.Id,
                    m.ContentType,
                    m.AuthorId,
                    IsPublic = m.Public
                })
                .SingleOrDefaultAsync(token);
            if (row is null)
                return Failure.NotFound;
            if (row.AuthorId != userId && !row.IsPublic)
                return Failure.NotPermitted;
            
            // drop to npgsql to enable streaming insert
            var conn = await ctx.GetPostgresConnectionAsync(token);
            var contentStream = await conn.TryToFetchMediaByIdAsync(row.Id, token);
            return contentStream.Map(s => new Object(row.ContentType, s));
        }

        /// <summary>
        /// Creates a new media entry in the database. On slug name conflict, a second attempt is made by appending
        /// a UUID to the slug name and retrying. Constraint failure errors are propagated in the return value.
        /// </summary>
        /// <param name="userId">user id of author</param>
        /// <param name="slug">media link slug</param>
        /// <param name="entry">file</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>the result of inserting, <see cref="Either"/> <see cref="Failure"/> or inserted slug name</returns>
        /// <exception cref="InvalidOperationException">if an internal error occurs during duplicate handling</exception>
        public async Task<Either<Failure, InsertResult>> CreateMediaEntryAsync(Guid userId, string slug, Object entry,
            CancellationToken token)
        {
            var toInsert = new Medium
            {
                AuthorId = userId,
                Slug = slug,
                ContentType = entry.ContentType,
                ContentLength = entry.ContentStream.Length.AssertLength(),
                Contents = entry.ContentStream
            };
            var validity = toInsert.CheckValidity();
            if (validity is not null)
                return validity.Value;
            
            // drop to npgsql to enable streaming insert
            var conn = await ctx.GetPostgresConnectionAsync(token);
            var insertResult = await conn.TryToInsertMediaAsync(toInsert, token: token);
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
            toInsert.AddV7UuidToSlugForConflictResolution();
            // i don't know if the postgres driver consumed the stream during the part of the insert that failed so
            // check for nonzero position and rewind if so
            if (toInsert.Contents.Position > 0)
                toInsert.Contents.Seek(0, SeekOrigin.Begin);
            insertResult = await conn.TryToInsertMediaAsync(toInsert, token);
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
        /// Updates object for medium. Will fail if slug not found or user isn't owner.
        /// </summary>
        /// <param name="userId">user id</param>
        /// <param name="slug">slug name</param>
        /// <param name="contents">new contents</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the <see cref="Failure"/>, if one occurred
        /// </returns>
        public async Task<Option<Failure>> UpdateMediaAsync(Guid userId, string slug, Object contents,
            CancellationToken token)
        {
            var row = await ctx.Media.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row == null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            
            // drop to npgsql to enable streaming insert
            var conn = await ctx.GetPostgresConnectionAsync(token);
            var updateResult = await conn.TryToUpdateMediaContentsAsync(userId, slug, contents, token);
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
        public async Task<Either<Failure, string>> RenameMediaSlugAsync(Guid userId, string oldSlug, string newSlug,
            CancellationToken token)
        {
            var row = await ctx.Media.SingleOrDefaultAsync(p => p.Slug == oldSlug, token);
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
        public async Task<Option<Failure>> UpdateMediaPermissionsAsync(Guid userId, string slug,
            Permissions permissions, CancellationToken token)
        {
            var row = await ctx.Media.SingleOrDefaultAsync(p => p.Slug == slug, token);
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
        public async Task<Either<Failure, Guid>> UpdateMediaAuthorAsync(Guid userId, string slug,
            string newUserEmail, CancellationToken token)
        {
            var row = await ctx.Media.SingleOrDefaultAsync(p => p.Slug == slug, token);
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
        /// Deletes a media entry by slug. Will fail if slug not found or user isn't author.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public async Task<Option<Failure>> DeleteMediaAsync(Guid userId, string slug, CancellationToken token)
        {
            var row = await ctx.Media.SingleOrDefaultAsync(p => p.Slug == slug, token);
            if (row is null)
                return Failure.NotFound;
            if (row.AuthorId != userId)
                return Failure.NotPermitted;
            ctx.Media.Remove(row);
            await ctx.SaveChangesAsync(token);
            return Option<Failure>.None;
        }
    }    
    
    extension(NpgsqlConnection pgConn)
    {
        /// <summary>
        /// Tries to read Medium contents (with cancellation).
        /// </summary>
        /// <param name="id">medium id</param>
        /// <param name="token">async cancellation token</param>
        /// <returns><see cref="Either"/> <see cref="Failure"/> or a read <see cref="Stream"/></returns>
        private async Task<Either<Failure, Stream>> TryToFetchMediaByIdAsync(Guid id, CancellationToken token)
        {
            const string query =
                """
                SELECT contents FROM media
                    WHERE id = @id
                """;
            await using var cmd = new NpgsqlCommand(query, pgConn);
            cmd.Parameters.AddWithValue("id", id);
            var reader = await cmd.ExecuteReaderAsync(token);
            if (!reader.HasRows)
                return Failure.NotFound;
            await reader.ReadAsync(token);
            var stream = await reader.GetStreamAsync(reader.GetOrdinal("contents"), token);
            return stream;
        }
        
        /// <summary>
        /// Tries to insert a Medium (with cancellation).
        /// </summary>
        /// <param name="medium">the medium to insert</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        private async Task<Option<Failure>> TryToInsertMediaAsync(Medium medium, CancellationToken token)
        {
            const string query =
                """
                    INSERT INTO media (slug, content_type, contents, content_length, author_id)
                    VALUES (@slug,  @content_type, @contents, @c_len, @author_id)
                """;
            await using var cmd = new NpgsqlCommand(query, pgConn);
            cmd.Parameters.AddWithValue("slug", medium.Slug);
            cmd.Parameters.AddWithValue("content_type", medium.ContentType);
            cmd.Parameters.AddWithValue("c_len", medium.ContentLength);
            cmd.Parameters.AddWithValue("contents", NpgsqlDbType.Bytea, medium.Contents);
            cmd.Parameters.AddWithValue("author_id", medium.AuthorId);
            try
            {
                var result = await cmd.ExecuteNonQueryAsync(token);
                return result == 1 ? Option<Failure>.None : Failure.Conflict;
            }
            catch (NpgsqlException e)
            {
                var asFailure = e.AsFailure();
                if (asFailure != default)
                    return asFailure;
                throw;
            }
        }
        
        /// <summary>
        /// Tries to update a Medium (with cancellation).
        /// </summary>
        /// <param name="userId">user id</param>
        /// <param name="slug">slug name</param>
        /// <param name="contents">new contents</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        private async Task<Option<Failure>> TryToUpdateMediaContentsAsync(Guid userId, string slug, Object contents,
            CancellationToken token)
        {
            const string query =
                """
                    UPDATE media SET contents = @contents, content_length = LENGTH(contents)
                    WHERE author_id = @author_id AND slug = @slug
                """;
            await using var cmd = new NpgsqlCommand(query, pgConn);
            cmd.Parameters.AddWithValue("slug", slug);
            cmd.Parameters.AddWithValue("contents", NpgsqlDbType.Bytea, contents.ContentStream);
            cmd.Parameters.AddWithValue("author_id", userId);
            
            try
            {
                var result = await cmd.ExecuteNonQueryAsync(token);
                // we would get a conflict if user id changed or slug no longer exists
                return result == 1 ? Option<Failure>.None : Failure.Conflict;
            }
            catch (NpgsqlException e)
            {
                var asFailure = e.AsFailure();
                if (asFailure != default)
                    return asFailure;
                throw;
            }
        }
    }

}

file static class RepositoryExtensionsHelpers
{
    extension(long l)
    {
        internal int AssertLength()
        {
            if (l > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(l));
            return (int)l;
        }
    }
    
    extension(Medium medium)
    {
        internal Failure? CheckValidity()
        {
            if (string.IsNullOrEmpty(medium.Slug))
                return Failure.Conflict;
            if (medium.Slug.Length > MEDIA_SLUG_MAXLEN)
                return Failure.TooLong;
            return null;
        }
        
        internal void AddV7UuidToSlugForConflictResolution()
        {
            var uuid = Guid.CreateVersion7();
            var uuidStr = $".{uuid:N}"; // hex digits, no punctuation
            
            var (name, ext) = RoutingExtensions.SplitFilenameComponents(medium.Slug);
            ext = '.' + ext;
            var reserveLen = uuidStr.Length + ext.Length; 
            
            // trim slug enough to prevent DB insert string length error
            // NOTE: this is a short string; no point in complexity of spans to remove just one alloc
            medium.Slug = name[..Math.Min(MEDIA_SLUG_MAXLEN - reserveLen, name.Length)] + uuidStr + ext;
        }
    }

    private const int MEDIA_SLUG_MAXLEN = 245;
}