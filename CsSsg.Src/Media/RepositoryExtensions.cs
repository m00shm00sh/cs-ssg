using System.Security.Claims;
using EntityFrameworkCore.Locking;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using CsSsg.Src.Post;
using static CsSsg.Src.Post.IManageCommand;
using static CsSsg.Src.Post.RepositoryExtensions;
using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.Media;

internal static class RepositoryExtensions
{
    extension(AppDbContext ctx)
    {
        /// <summary>
        /// Gets metadata for a slug given user id.
        /// </summary>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>Media's <see cref="AccessLevel"/> if found, otherwise <c>null</c></returns>
        public async Task<(Entry, ConcurrencyToken)?> GetMetadataForMediaAsync(string slug, CancellationToken token)
        {
            var row = await ctx.Media
                .Where(m => m.Slug == slug)
                .Include(p => p.Tags)
                .Include(m => m.LatestRevision)
                .Include(m => m.MediaRevisions)
                .Select(m => new
                {
                    m.AuthorId,
                    ContentType = m.LatestRevision != null ? m.LatestRevision.ContentType : null!,
                    Size = m.LatestRevision != null ? (long?)m.LatestRevision.ContentLength : null,
                    m.UpdatedAt,
                    Tags = m.Tags.Select(t => t.Tag).ToList(),
                    Revisions = m.MediaRevisions.Select(r => new
                    {
                        r.ContentType,
                        r.ContentLength,
                        r.AuthorId,
                        r.CreatedAt
                    }),
                    ConcurrencyToken = new ConcurrencyToken(m.PVer)
                })
                .SingleOrDefaultAsync(cancellationToken: token);
            if (row is null)
                return null;

            if (row.ContentType == null || row.Size == null)
                throw new InvalidOperationException($"latest revision is null for slug {slug}");
            
            var entry = new Entry
            {
                ContentType = row.ContentType,
                AuthorId = row.AuthorId,
                Size = row.Size.Value,
                Tags = row.Tags,
                LastModified = row.UpdatedAt
            };
            return (entry, row.ConcurrencyToken);
        }

        /// <summary>
        /// Queries modify timestamp for slug.
        /// </summary>
        /// <param name="slug">slug name</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>an Optional of <see cref="DateTime"/> or <c>None</c></returns>
        public async Task<Option<DateTimeOffset>> GetModifyTimeForMediaAsync(string slug, CancellationToken token)
        {
            var row = await ctx.Media
                .Where(m => m.Slug == slug)
                .Select(m => m.UpdatedAt)
                .SingleOrDefaultAsync(cancellationToken: token);
            return row != default ? (DateTimeOffset)row.ToUniversalTime() : Option<DateTimeOffset>.None;
        }
        /// <summary>
        /// Lists the content entries owned by the given user.
        /// </summary>
        /// <param name="user">user identity of listing accessor</param>
        /// <param name="filterTags">secondary filtering tags</param>
        /// <param name="beforeOrAt">(pagination) timestamp to not query more recent than</param>
        /// <param name="limit">(pagination) maximum number of posts</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a List of <see cref="Entry"/> </returns>
        public async Task<List<Entry>> GetAllMediaForOwnerAsync(ClaimsPrincipal user, ICollection<string> filterTags,
            DateTimeOffset beforeOrAt, int limit, CancellationToken token)
        {
            if (!user.TryGetUidAndSave(out var userId))
                return [];

            var query = ctx.Media.AsNoTracking()
                .Include(m => m.Tags)
                .Include(m => m.Author)
                as IQueryable<Medium>;
            if (filterTags.Count > 0)
                query = query.Where(m => m.Tags.Count(t => filterTags.Contains(t.Tag)) > 0);
            
            query = query
                .Where(m => m.UpdatedAt < beforeOrAt)
                .Where(m => m.AuthorId == userId)
                .Include(m => m.MediaRevisions)
                .Include(m => m.LatestRevision)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(limit);
            
            var result = await query.Select(m => new Entry
                {
                    Slug = m.Slug,
                    ContentType = m.LatestRevision != null ? m.LatestRevision.ContentType : null!,
                    Size = m.LatestRevision != null ? m.LatestRevision.ContentLength : -1,
                    AuthorHandle = m.Author.Email,
                    AccessLevel = AccessLevel.FullControl,
                    Tags = m.Tags.Select(t => t.Tag).ToList(),
                    LastModified = m.UpdatedAt,
                }
            ).ToListAsync(token);
            foreach (var entry in result)
            {
                if (entry.Size < 0)
                    throw new InvalidOperationException($"for slug {entry.Slug} we have a null LatestRevision");
            }

            return result;
        }

        /// <summary>
        /// Fetches content data. Will fail if post is missing.
        /// </summary>
        /// <param name="slug">slug (link) of post</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>the result of fetching, <see cref="Either"/> <see cref="Failure"/> or the object</returns>
        public async Task<Either<Failure, Object>> GetObjectForSlug(string slug, ConcurrencyToken cToken,
            CancellationToken token)
        {
            var row = await ctx.Media
                .AsNoTracking()
                .Where(m => m.Slug == slug)
                .Include(m => m.LatestRevision)
                .Select(m => new
                {
                    m.LatestRevision,
                    m.AuthorId,
                    ModifyTime = m.UpdatedAt,
                    ConcurrencyToken = new ConcurrencyToken(m.PVer)
                })
                .SingleOrDefaultAsync(token);
            if (row is null)
                return Failure.NotFound;
            if (row.ConcurrencyToken != cToken)
                return Failure.Conflict;
            
            if (row.LatestRevision == null)
                throw new InvalidOperationException($"for slug {slug} we have a null LatestRevision");
            var streamId = row.LatestRevision.Id;
            var contentType = row.LatestRevision.ContentType;
                
            // drop to npgsql to enable streaming insert
            var conn = await ctx.GetPostgresConnectionAsync(token);
            var contentStream = await conn.TryToFetchMediaByRevisionIdAsync(streamId, token);
            return contentStream.Map(s => new Object(contentType, s, lastModified: row.ModifyTime));
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
            var toInsert = entry.ToDbRow(userId, slug);
            var validity = toInsert.CheckValidity();
            if (validity is not null)
                return validity.Value;
            var revision = entry.ToRevisionRow(userId);

            await using var tx = await ctx.Database.BeginTransactionAsync(token);
            
            var insertResult = await ctx.TryToInsertMediaRowAsync(toInsert, rollbackOnFailure: true,
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

            var didResolveDuplicate = false;
            
            if (!retryWithUuid)
                goto insertRevision;
           
            RepositoryExtensionsHelpers.AddV7UuidToSlugForConflictResolution(toInsert);
            insertResult = await ctx.TryToInsertMediaRowAsync(toInsert, token);
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
            didResolveDuplicate = true;
            
        insertRevision:
            // force a no-tracking select to lock the row (we will reuse the inserted row for changes)
            await ctx.Media.AsNoTracking()
                .Where(m => m.Id == toInsert.Id)
                .ForUpdate()
                .SingleAsync(token);
            
            // drop to npgsql to enable streaming insert
            var conn = await ctx.GetPostgresConnectionAsync(token);
            var revisionResult = await conn.InsertMediaLatestRevisionAsync(revision, toInsert.Id, userId, token);

            switch (revisionResult.Case)
            {
                case Failure revInsertFailure:
                    return revInsertFailure;
                case Guid revId:
                    toInsert.LatestRevisionId = revId;
                    toInsert.LatestRevisionAuthorId = userId;
                    var updateResult = await ctx.TryToCommitChangesAsync(token);
                    if (updateResult.IsSome)
                        return (Failure)updateResult.Case!;
                    break;
            }
            
            await tx.CommitAsync(token);
            return insertResult.ToEither(new InsertResult(toInsert.Slug, didResolveDuplicate)).Swap();
        }


        /// <summary>
        /// Tries to insert a Medium (with cancellation) and roll back the entity tracking on failure if desired.
        /// </summary>
        /// <param name="row">the medium to insert</param>
        /// <param name="token">async cancellation token</param>
        /// <param name="rollbackOnFailure">if true, simulate a rollback on failure by discarding the attempt</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        private async Task<Option<Failure>> TryToInsertMediaRowAsync(Medium row, CancellationToken token,
            bool rollbackOnFailure = false)
        {
            if (row.MediaRevisions.Count > 0)
                throw new InvalidOperationException("row cannot have queued revisions");
            
            var rowMeta = await ctx.Media.AddAsync(row, token);
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
        ///     Updates object for medium.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id</param>
        /// <param name="slug">slug name</param>
        /// <param name="contents">new contents</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the <see cref="Failure"/>, if one occurred
        /// </returns>
        public async Task<Option<Failure>> UpdateMediaAsync(Guid userId, string slug, Object contents,
            ConcurrencyToken cToken, CancellationToken token)
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(token);

            var row = await ctx.Media
                .Where(p => p.Slug == slug)
                .ForUpdate()
                .SingleOrDefaultAsync(token);
            if (row == null)
                return Failure.NotFound;
            if (row.PVer != cToken.Value)
                return Failure.Conflict;
            
            var revision = contents.ToRevisionRow(userId);
            
            // drop to npgsql to enable streaming insert
            var conn = await ctx.GetPostgresConnectionAsync(token);
            
            var revisionResult = await conn.InsertMediaLatestRevisionAsync(revision, row.Id, userId, token);
            var updateResult = Option<Failure>.None;

            switch (revisionResult.Case)
            {
                case Failure revInsertFailure:
                    return revInsertFailure;
                case Guid revId:
                    row.LatestRevisionId = revId;
                    row.LatestRevisionAuthorId = userId;
                    updateResult = await ctx.TryToCommitChangesAsync(token);
                    if (updateResult.IsSome)
                        return (Failure)updateResult.Case!;
                    break;
            }

            await tx.CommitAsync(token);
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
        public Task<Either<Failure, string>> RenameMediaSlugAsync(Guid userId, string oldSlug, string newSlug,
            ConcurrencyToken cToken, CancellationToken token)
            => ctx.DoUpdateSlugAsync(ctx.Media, userId, oldSlug, newSlug,
                RepositoryExtensionsHelpers.AddV7UuidToSlugForConflictResolution, cToken, token);
        
        /// <summary>
        ///     Modifies the permission tags of a post.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="tags">the new <see cref="PostTags"/> to set</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public Task<Option<Failure>> UpdateMediaTagsAsync(Guid userId, string slug,
            PostTags tags, ConcurrencyToken cToken, CancellationToken token)
            => ctx.DoUpdateTagsAsync<Medium, MediaTag>(
                ctx.Media, userId, slug, tags, cToken, token); 
        
        /// <summary>
        ///     Modifies the author of a post.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// New author is returned on success.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="newUserEmail">email of new author</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the result of changing author,
        ///     <see cref="Either"/> <see cref="Failure"/> or new author's <see cref="Guid"/>
        /// </returns>
        public Task<Either<Failure, Guid>> UpdateMediaAuthorAsync(Guid userId, string slug,
            string newUserEmail, ConcurrencyToken cToken, CancellationToken token)
            => ctx.DoUpdateAuthorAsync(ctx.Media, userId, slug, newUserEmail, cToken, token);

        /// <summary>
        ///     Deletes a media entry by slug.
        ///     Will fail if slug not found or row state doesn't indicate successful permissions check.
        /// </summary>
        /// <param name="userId">user id of update author</param>
        /// <param name="slug">the slug to update</param>
        /// <param name="cToken">concurrent change detection token</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public Task<Option<Failure>> DeleteMediaAsync(Guid userId, string slug, ConcurrencyToken cToken,
            CancellationToken token)
            => ctx.DoDeleteContentAsync(ctx.Media, userId, slug, cToken, token);
    }    
    
    extension(NpgsqlConnection pgConn)
    {
        /// <summary>
        /// Tries to read Medium contents (with cancellation).
        /// </summary>
        /// <param name="id">medium id</param>
        /// <param name="token">async cancellation token</param>
        /// <returns><see cref="Either"/> <see cref="Failure"/> or a read <see cref="Stream"/></returns>
        private async Task<Either<Failure, Stream>> TryToFetchMediaByRevisionIdAsync(Guid id, CancellationToken token)
        {
            const string query =
                """
                SELECT contents FROM media_revisions
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
        /// Inserts the last revision of media (with cancellation).
        /// <remarks>It does not insert or update the media row itself.</remarks>
        /// </summary>
        /// <param name="revision">the revision to insert</param>
        /// <param name="mediaId">media id</param>
        /// <param name="authorId">revision author id</param>
        /// <param name="token">async cancellation token</param>
        /// <returns><see cref="Either"/> <see cref="Failure"/>, if any occurred or inserted <see cref="Guid"/></returns>
        private async Task<Either<Failure, Guid>> InsertMediaLatestRevisionAsync(MediaRevision revision, Guid mediaId, 
            Guid authorId, CancellationToken token)
        {
            const string query =
                """
                    INSERT INTO media_revisions (content_type, contents, content_length, author_id, media_id)
                    VALUES (@content_type, @contents, @c_len, @author_id, @media_id)
                    RETURNING id
                """;
            await using var cmd = new NpgsqlCommand(query, pgConn);
            cmd.Parameters.AddWithValue("content_type", revision.ContentType);
            cmd.Parameters.AddWithValue("c_len", revision.ContentLength);
            cmd.Parameters.AddWithValue("contents", NpgsqlDbType.Bytea, revision.Contents);
            cmd.Parameters.AddWithValue("author_id", authorId);
            cmd.Parameters.AddWithValue("media_id", mediaId);
            try
            {
                return await cmd.ExecuteScalarAsync(token) is Guid result ? result : Failure.Conflict;
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

    extension(Object entry)
    {
        internal Medium ToDbRow(Guid authorId, string slug)
            => new()
            {
                Slug = slug,
                AuthorId = authorId,
            };

        internal MediaRevision ToRevisionRow(Guid authorId)
            => new()
            {
                ContentType = entry.ContentType,
                ContentLength = entry.ContentStream.Length.AssertLength(),
                Contents = entry.ContentStream,
                AuthorId = authorId
            };
    }

    extension(Medium medium)
    {
        internal Failure? CheckValidity()
        {
            var lastRev = medium.MediaRevisions.Last();
            if (string.IsNullOrWhiteSpace(lastRev.ContentType))
                return Failure.Conflict;
            if (lastRev.ContentType.Length > MEDIA_CTYPE_MAXLEN)
                return Failure.TooLong;
            if (string.IsNullOrEmpty(medium.Slug))
                return Failure.Conflict;
            if (medium.Slug.Length > MEDIA_SLUG_MAXLEN)
                return Failure.TooLong;
            return null;
        }
    }
    
    internal static void AddV7UuidToSlugForConflictResolution(Medium medium)
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

    private const int MEDIA_SLUG_MAXLEN = 245;
    private const int MEDIA_CTYPE_MAXLEN = 255;
}