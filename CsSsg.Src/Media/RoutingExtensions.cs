using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using LanguageExt;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using static CsSsg.Src.Media.FilterConfigurationExtensions;
using CsSsg.Src.Post;
using static CsSsg.Src.Post.RepositoryExtensions;
using CsSsg.Src.Program;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.User;

namespace CsSsg.Src.Media;

internal static partial class RoutingExtensions
{
    
    internal const string MEDIA_PREFIX = "/media";
    private const string RX_OPT_UUID = @"(\.[0-9a-f]{{32}})?";
    private const string SLUG = @"\w+(-\w+)*";
    private const string RX_SLUG_WITH_OPT_UUID = $@"^{SLUG}{RX_OPT_UUID}(\.{SLUG})?$";
    [StringSyntax("Route")]
    private const string NAME_SLUG = $"/{{name:regex({RX_SLUG_WITH_OPT_UUID})}}";
    
    extension(WebApplication app)
    {
        public void AddMediaRoutes(Features flags, string apiPrefix)
        {
            flags.Gate(Features.HtmlApi, app.AddMediaHtmlRoutes);
            flags.Gate(Features.JsonApi, () => app.AddMediaJsonRoutes(apiPrefix));
        }
    }

    private static class CacheHelpers
    {
        internal static string ListingKey(Guid? uid, DateTimeOffset dateUtc, int limit)
        {
            return $"listing-media/{uid};{dateUtc};{limit}";
        }
        
        internal static List<string> ListingTags(Guid? uid, bool isPublic)
        {
            List<string> tags = [];
            if (isPublic) tags.Add("listing-media");
            if (uid is not null) tags.Add($"listing-media/{uid}");
            return tags;
        }
    }

    /// <summary>
    ///     This is a wrapper for DoGetMediaAsync that saves the modify time and re-types `IResult` to `Results&lt;...>`
    ///     for clarity.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     thrown by the resulting function if an internal state was unhandled
    /// </exception>
    private static async Task<Results<FileStreamHttpResult, ForbidHttpResult, NotFound>> InvokeDoGetMediaAsync(
        string name, HttpContext ctx, AppDbContext repo, IFusionCache cache, CancellationToken token, int revision = 0)
    {
        var cToken = ctx.RequireConcurrencyToken();
        var result = await DoGetMediaForNameAsync(name, cToken, repo, cache, token, revision);
        if (result is FileStreamHttpResult { LastModified: not null } fs) 
            ctx.SetModifiedSinceValue(fs.LastModified.Value.UtcDateTime);
        return result switch
        {
            FileStreamHttpResult file => file,
            ForbidHttpResult _403 => _403,
            NotFound _404 => _404,
            _ => throw new InvalidOperationException($"unhandled result type {result.GetType()}")
        };
    }

    /// <summary>
    /// Get the media referred to by slug name, if allowed.
    /// </summary>
    /// <param name="slug">slug name</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="cToken">concurrent change detection token</param>
    /// <param name="token">async cancellation token</param>
    /// <param name="revision">optional revision number</param>
    /// <returns>
    ///     <list>
    ///         <item>a <see cref="FileStreamHttpResult"/> on success</item>
    ///         <item>a <see cref="ForbidHttpResult"/> if access is not permitted</item>
    ///         <item>a <see cref="NotFound"/> if the content doesn't exist</item>
    ///         <item>a <see cref="Conflict"/> if there's a race condition between metadata and content fetch</item>
    ///     </list>
    /// </returns>
    public static async Task<IResult> DoGetMediaForNameAsync(string slug, ConcurrencyToken cToken, AppDbContext repo,
        IFusionCache cache, CancellationToken token, int revision = 0)
        // TODO: caching
        => (await repo.GetObjectForSlug(slug, cToken, token, revision))
            .Match<IResult>(o => 
                TypedResults.Stream(
                    o.ContentStream,
                    contentType: o.ContentType,
                    lastModified: o.LastModified),
                FailureExtensions.AsResult);

    /// <summary>
    /// Commits an update to media object.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="user">user identity of committer</param>
    /// <param name="contents">new contents</param>
    /// <param name="isPublic">whether the post is public (only affects cache invalidations)</param>
    /// <param name="cToken">concurrent change detection token</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoSubmitMediaEditForNameAsync(
        string name, ClaimsPrincipal user, Object contents, bool isPublic, ConcurrencyToken cToken,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var sizeLimit = await repo.GetUserMediaUploadSizeLimitAsync(user, token);
        var uid = user.TryGetUid() ?? Guid.Empty;
        if (await contents.BufferIfNotSeekableAsync(sizeLimit, token) is { } o)
            contents = o;
        else
        {
            // the drain on the buffering operation failed because reading was done past the configured limit
            return Failure.TooLong;
        }
        var contentLength = contents.ContentStream.Length;
        if (sizeLimit < contentLength)
            return Failure.TooLong;
        
        if ((await repo.UpdateMediaAsync(uid, name, contents, cToken, token)).ToNullable() is { } f)
            return f;
        RoutingLogging.LogUpdater_CommitBySlugName(logger, name);
        RoutingLogging.LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(logger, "updater", 
            name, uid, isPublic);
        await cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, isPublic), token: token);
        return Option<Failure>.None;
    }
    
    /// <summary>
    /// Creates a new media object, resolving duplicate slug name if applicable. 
    /// </summary>
    /// <param name="filename">file name</param>
    /// <param name="mEntry">new contents</param>
    /// <param name="user">author identity</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>the result of creating, <see cref="Either"/> <see cref="Failure"/> or inserted slug name</returns>
    /// <remarks>
    ///     This function slugifies the filename parameter. There is no need to supply a pre-slugified name.
    /// </remarks>
    public static async Task<Either<Failure, string>> DoSubmitMediaCreationAsync(string filename, Object mEntry,
        ClaimsPrincipal user, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = user.TryGetUid() ?? Guid.Empty;
        var sizeLimit = await repo.GetUserMediaUploadSizeLimitAsync(user, token);
        if (await mEntry.BufferIfNotSeekableAsync(sizeLimit, token) is { } o)
            mEntry = o;
        else
        {
            // the drain on the buffering operation failed because reading was done past the configured limit
            return Failure.TooLong;
        }
        var contentLength = mEntry.ContentStream.Length;
        if (sizeLimit < contentLength)
            return Failure.TooLong;
        
        filename = SlugifyFilename(filename);
        RoutingLogging.LogSubmitNew_ForNameWithUidAndPublic(logger, filename, uid);
        var insertStatus = await repo.CreateMediaEntryAsync(uid, filename, mEntry, token);
        RoutingLogging.LogSubmitNew_InsertResultByStatus(logger, insertStatus);
        var insertResult = default(InsertResult);
        var failCode = default(Failure);
        insertStatus.Match(
            inserted => insertResult = inserted,
            f => failCode = f
        );
        if (failCode != default)
            return failCode;
        await _clearCacheEntriesAsync(cache, logger, insertResult, token);
        if (!insertResult.DidDuplicateResolution)
            await ContentAccessPermissionFilter.InvalidateAccessCacheForKeyAsync(logger, cache, 
                ContentAccessFilterConfig, "insert", insertResult.InsertedName, token);
        // we don't invalidate the listing caches because the insert won't cause the cached snapshot to become invalid
        // (unlike temporal or permissions update)
        return insertResult.InsertedName;
    }

    /// <summary>
    /// Renders <see cref="IManageCommand.Stats"/> for a post.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="tags">medium's current permission tags (to be supplied by caller)</param>
    /// <param name="cToken">concurrent change detection token</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>the <see cref="Stats"/> for the post referenced by slug</returns>
    /// <exception cref="InvalidOperationException">if there was an internal error due to missing middleware filtering</exception>
    public static async Task<Stats> DoGetManagePageForNameAndPermissionAsync(
        string name, IManageCommand.PostTags tags, ConcurrencyToken cToken,
        AppDbContext repo, IFusionCache cache, CancellationToken token)
    {
        // todo: caching
        var xmeta = await repo.GetMetadataForMediaAsync(name, token);
        if (xmeta is null)
            throw new InvalidOperationException("middleware did not catch a missing entry");
        var (meta, actualCToken) = xmeta.Value;
        if (actualCToken != cToken)
            throw new InvalidOperationException("concurrency conflict detected");
        return new Stats
        {
            ContentType = meta.ContentType,
            Size = meta.Size,
            Tags = tags
        };
    }

    /// <summary>
    /// Submits a rename for a medium.
    /// </summary>
    /// <param name="name">(old) slug name</param>
    /// <param name="uid">author id</param>
    /// <param name="renameCommand">rename destination details</param>
    /// <param name="cToken">concurrent change detection token</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>
    ///     the result of renaming with duplicate slug resolution,
    ///     <see cref="Either"/> <see cref="Failure"/> or new slug name
    /// </returns>
    public static async Task<Either<Failure, string>> DoSubmitRenameForNameAsync(
        string name, Guid uid, IManageCommand.Rename renameCommand, ConcurrencyToken cToken,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var newSlug = SlugifyFilename(renameCommand.RenameTo);
        RoutingLogging.LogSubmitManage_RenameBySlug(logger, name, uid, newSlug);
        var renameResult = await repo.RenameMediaSlugAsync(uid, name, newSlug, cToken, token);
        RoutingLogging.LogSubmitManage_RenameResultByStatus(logger, renameResult);
        
        if (renameResult.IsRight)
            // invalidate cache entries related to old name
            await Task.WhenAll(
                    ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache,
                        ContentAccessFilterConfig, "manager:rename", token),
                    _clearCacheEntriesAsync(cache, logger, new InsertResult(name, false), token));
        return renameResult;
    }

    /// <summary>
    /// Submits a change of permissions for a medium.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="uid">author id</param>
    /// <param name="tagsCommand">new permissions</param>
    /// <param name="cToken">concurrent change detection token</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoSubmitChangeTagsForNameAsync(
        string name, Guid uid, IManageCommand.SetTags tagsCommand, ConcurrencyToken cToken,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var newTags = tagsCommand.Tags;
        if (newTags.Visibility == IManageCommand.PostVisibility.Public)
            throw new ArgumentOutOfRangeException(nameof(tagsCommand), "invalid: media && visibility=public");
        RoutingLogging.LogSubmitManage_ChangeTagsBySlug(logger, name, uid, newTags);
        var changeTagsResult = await repo.UpdateMediaTagsAsync(uid, name, newTags, cToken, token);
        RoutingLogging.LogSubmitManage_ChangeTagsResultByStatus(logger, changeTagsResult);
        
        if (changeTagsResult.IsNone)
        {
            await ContentAccessPermissionFilter.InvalidateAccessCacheForKeyAsync(logger, cache, 
                ContentAccessFilterConfig, "manager:chperm", name, token);
            if (newTags.Visibility != IManageCommand.PostVisibility.Public)
            {
                await Task.WhenAll(
                    cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, false), token: token)
                        .AsTask(),
                    ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache,
                        ContentAccessFilterConfig, "manager:chperm -public", token)
                );
            }
        }
        return changeTagsResult;
    }
   
    /// <summary>
    /// Submits a change of author for a medium.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="uid">author id</param>
    /// <param name="isPublic">true if the post has anonymous read/listable permissions</param>
    /// <param name="authorCommand">new author details</param>
    /// <param name="cToken">concurrent change detection token</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>
    ///     the result of changing author,
    ///     <see cref="Either"/> <see cref="Failure"/> or new author's <see cref="Guid"/>
    /// </returns>
    public static async Task<Either<Failure, Guid>> DoSubmitSetAuthorForNameAsync(
        string name, Guid uid, bool isPublic, IManageCommand.SetAuthor authorCommand, ConcurrencyToken cToken,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var newAuthor = authorCommand.NewAuthor;
        RoutingLogging.LogSubmitManage_ChangeAuthorBySlug(logger, name, uid, newAuthor);
        var changeAuthorResult = await repo.UpdateMediaAuthorAsync(uid, name, newAuthor, cToken, token);
        RoutingLogging.LogSubmitManage_ChangeAuthorResultByStatus(logger, changeAuthorResult);
        if (changeAuthorResult.IsRight)
        {
            var newAuthorId = (Guid)changeAuthorResult.Case;
            // we only need to invalidate the perms and listing caches if author changes for private post
            if (!isPublic)
            {
                RoutingLogging.LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(logger,
                    "manager:chauthor", name, uid, false);
                await Task.WhenAll(
                    cache.RemoveByTagAsync(
                    [
                        ..CacheHelpers.ListingTags(uid, false),
                        ..CacheHelpers.ListingTags(newAuthorId, false)
                    ], token: token).AsTask(),
                    ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache,
                        ContentAccessFilterConfig, "manager:chauthor", token));
            }
        }
        return changeAuthorResult;
    }
    
    /// <summary>
    /// Submits a deletion request for a medium.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="isPublic">true if the medium has anonymous read/listable permissions</param>
    /// <param name="uid">author id</param>
    /// <param name="cToken">concurrent change detection token</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoDeleteMediumAsync(
        string name, bool isPublic, Guid uid, ConcurrencyToken cToken,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        RoutingLogging.LogSubmitManage_ExecuteDeleteForSlug(logger, name, uid);
        var execDeleteResult = await repo.DeleteMediaAsync(uid, name, cToken, token);
        RoutingLogging.LogSubmitManage_DeleteResultByStatus(logger, execDeleteResult);
        // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
        await execDeleteResult.IfNoneAsync(async () =>
        {
            RoutingLogging.LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(logger,
                "manager:chauthor", name, uid, false);
            await Task.WhenAll(
                cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, isPublic), token: token)
                    .AsTask(),
                ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache, 
                    ContentAccessFilterConfig,"manager:delete", token),
                _clearCacheEntriesAsync(cache, logger, new InsertResult(name, false), token)
            );
            return default;
        });
        return execDeleteResult;
    }
    
    // the HtmlApi and JsonApi versions differ only in terms of output rendering and have the same logic in the middle
    // so wrap the unified path in a function
    private static Func<ClaimsPrincipal, AppDbContext, IFusionCache, CancellationToken, int, string?, string[],
            Task<TR>> 
    ExtractUidFromClaimsThenInvokeGetAllAvailableMediaThenTransformResult<TR>(
        Func<IEnumerable<Entry>, TR> renderer)
        => async (ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, CancellationToken token,
                // suppress CS9099 because ASP.NET's reflection scans the lambda type not the delegate type for binding
                // and optionals
                #pragma warning disable CS9099
                [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null, [FromQuery] string[] xtags = null!) =>
                #pragma warning restore CS9099
        {
            xtags ??= [];
            auth.RequireUid();
            var date = beforeOrAt is null
                ? DateTime.UtcNow
                : DateTime.Parse(beforeOrAt, null, DateTimeStyles.RoundtripKind);

            var listing = await DoGetAllAvailableMediaEntriesForUserAsync(auth, limit, date, 
                repo, cache, token, xtags);
            return renderer(listing);
        };
        
    /// <summary>
    /// Lists the media entries owned by the given user. 
    /// </summary>
    /// <param name="user">identity of listing accessor</param>
    /// <param name="limit">(pagination) maximum number of posts</param>
    /// <param name="beforeOrAtUtc">(pagination) timestamp to not query more recent than</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <param name="xTags">secondary filtering tags</param>
    /// <returns>a List of <see cref="Entry"/></returns>
    public static async Task<IEnumerable<Entry>> DoGetAllAvailableMediaEntriesForUserAsync(
        ClaimsPrincipal user, int limit, DateTimeOffset beforeOrAtUtc, AppDbContext repo, IFusionCache cache, 
        CancellationToken token, IList<string> xTags = null!)
    {
        var uid = user.RequireUid();
        xTags ??= [];
        for (var i = 0; i < xTags.Count; i++)
            xTags[i] = xTags[i].ToLower();
        
        var listing = await cache.GetOrSetAsync(CacheHelpers.ListingKey(uid, beforeOrAtUtc, limit),
            _ => repo.GetAllMediaForOwnerAsync(user, xTags, beforeOrAtUtc, limit, token),
            tags: CacheHelpers.ListingTags(uid, false), token: token);
        return listing;
    }
    
    private static async Task _clearCacheEntriesAsync(IFusionCache cache, ILogger<Routing> logger,
        InsertResult insertResult, CancellationToken token)
    {
        RoutingLogging.LogMediaCacher_ClearForSlug(logger, insertResult.InsertedName);
        // TODO: content caching
    }

    internal static (string, string) SplitFilenameComponents(string filename)
    {
        var ext = "";
        var dot = filename.LastIndexOf('.');

        if (dot != -1 && filename.Length > dot + 1)
        {
            ext = filename[(dot + 1)..].ToLower();
            filename = filename[..dot];
        }

        filename = Contents.ComputeSlugName(filename);
        if (ext.Length > 0)
            ext = Contents.ComputeSlugName(ext);

        return (filename, ext);
    }
    
    internal static string SlugifyFilename(string filename)
    {
        var (name, ext) = SplitFilenameComponents(filename);
        return ext.Length > 0 ? name + '.' + ext : name;
    }
}

internal static partial class RoutingLogging
{
    [LoggerMessage(LogLevel.Debug, "content cacher: set slug {name}")]
    internal static partial void LogContentCacher_SetForSlug(ILogger<Routing> logger, string name);
    
    [LoggerMessage(LogLevel.Debug, "content cacher: clear slug {name}")]
    internal static partial void LogMediaCacher_ClearForSlug(ILogger<Routing> logger, string name);
    
    [LoggerMessage(LogLevel.Debug, "{context}: slug {name}: invalidate cache: uid={uid} public={isPublic}")]
    internal static partial void LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(ILogger<Routing> logger, 
        string context, string name, Guid uid, bool isPublic);
   
    [LoggerMessage(LogLevel.Information, "updater: commit slug {name}")]
    internal static partial void LogUpdater_CommitBySlugName(ILogger<Routing> logger, string name);

    [LoggerMessage(LogLevel.Information, "submit new: filename {name} from {uid}")]
    internal static partial void LogSubmitNew_ForNameWithUidAndPublic(ILogger<Routing> logger,
        string name, Guid uid);

    [LoggerMessage(LogLevel.Debug, "insert result: {insertStatus}")]
    internal static partial void LogSubmitNew_InsertResultByStatus(ILogger<Routing> logger, 
        Either<Failure, InsertResult> insertStatus);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: rename to {newName}")]
    internal static partial void LogSubmitManage_RenameBySlug(ILogger<Routing> logger,
        string name, Guid uid, string newName);

    [LoggerMessage(LogLevel.Debug, "rename result: {renameStatus}")]
    internal static partial void LogSubmitManage_RenameResultByStatus(ILogger<Routing> logger,
        Either<Failure, string> renameStatus);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: change permission to {newTags}")]
    internal static partial void LogSubmitManage_ChangeTagsBySlug(ILogger<Routing> logger,
        string name, Guid uid, IManageCommand.PostTags newTags);
    
    [LoggerMessage(LogLevel.Debug, "change permission result: {status}")]
    internal static partial void LogSubmitManage_ChangeTagsResultByStatus(ILogger<Routing> logger,
        Option<Failure> status);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: change owner to email={newAuthor}")]
    internal static partial void LogSubmitManage_ChangeAuthorBySlug(ILogger<Routing> logger,
        string name, Guid uid, string newAuthor);
    
    [LoggerMessage(LogLevel.Debug, "change author result: {authorResult}")]
    internal static partial void LogSubmitManage_ChangeAuthorResultByStatus(ILogger<Routing> logger,
        Either<Failure, Guid> authorResult);
    
    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: execute delete")]
    internal static partial void LogSubmitManage_ExecuteDeleteForSlug(ILogger<Routing> logger, string name, Guid uid);
    
    [LoggerMessage(LogLevel.Debug, "delete result: {status}")]
    internal static partial void LogSubmitManage_DeleteResultByStatus(ILogger<Routing> logger, Option<Failure> status);
}

/// <summary>
/// Tag class for logger used for post routing.
/// </summary>
internal abstract class Routing;
