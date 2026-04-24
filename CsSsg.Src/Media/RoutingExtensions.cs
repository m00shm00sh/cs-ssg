using System.Diagnostics;
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
using CsSsg.Src.Program;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.User;

namespace CsSsg.Src.Media;

internal static partial class RoutingExtensions
{
    
    internal const string MEDIA_PREFIX = "/media";
    private const string RX_OPT_UUID = @"(\.[[0-9a-f]]{{32}})?";
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
        internal static string ListingKey(Guid? uid, DateTime dateUtc, int limit)
        {
            Debug.Assert(dateUtc.ToUniversalTime() == dateUtc, "datetime is not in utc format");
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

    // the HtmlApi and JsonApi versions differ only in terms of authentication handling and have the same code paths
    // afterward so wrap the unified path in a function and capture the authentication-related extractor
    /// <summary>
    /// This is a function factory that saves the auth extractor, producing the DoGetMediaAsync endpoint function
    /// in the process. The signature of the resulting endpoint is
    /// <code>
    ///     Task&lt;Results&lt;FileStreamHttpResult, ForbidHttpResult, NotFound>>(
    ///         string name, ClaimsPrincipal?, AppDbContext, IFusionCache, CancellationToken)
    /// </code>
    /// </summary>
    /// <param name="uidExtractor">
    ///     the function that takes the optional authentication claims and extract the uid
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     thrown by the resulting function if an internal state was unhandled
    /// </exception>
    private static Func<string, ClaimsPrincipal?, AppDbContext, IFusionCache, CancellationToken,
            Task<Results<FileStreamHttpResult, ForbidHttpResult, NotFound>>>
    TryExtractUidFromOptionalClaimsThenInvokeDoGetMediaAsync(Func<ClaimsPrincipal?, Guid?> uidExtractor)
        => async (name, auth, repo, cache, token) =>
        {
            {
                var uidFromAuth = uidExtractor(auth);
                var result = await DoGetMediaForNameAsync(name, uidFromAuth, repo, cache, token);
                return result switch
                {
                    FileStreamHttpResult file => file,
                    ForbidHttpResult _403 => _403,
                    NotFound _404 => _404,
                    _ => throw new InvalidOperationException($"unhandled result type {result.GetType()}")
                };
            }
        };

    /// <summary>
    /// Get the media referred to by slug name, if allowed.
    /// </summary>
    /// <param name="slug">slug name</param>
    /// <param name="loggedInUid">logged in user (or <c>null</c>)</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>
    ///     <list>
    ///         <item>a <see cref="FileStreamHttpResult"/> on success</item>
    ///         <item>a <see cref="ForbidHttpResult"/> if access is not permitted</item>
    ///         <item>a <see cref="NotFound"/> if the content doesn't exist</item>
    ///     </list>
    /// </returns>
    public static async Task<IResult> DoGetMediaForNameAsync(string slug, Guid? loggedInUid, AppDbContext repo,
            IFusionCache cache, CancellationToken token)
        // TODO: caching
        // TODO: conditional if-modified-since
        => (await repo.GetObjectForSlug(loggedInUid, slug, token))
            .Match<IResult>(o => TypedResults.Stream(o.ContentStream, contentType: o.ContentType),
                FailureExtensions.AsResult);
    
    /// <summary>
    /// Commits an update to media object.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="uid">committer id</param>
    /// <param name="contents">new contents</param>
    /// <param name="isPublic">whether the post is public (only affects cache invalidations)</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoSubmitMediaEditForNameAsync(
        string name, Guid uid, Object contents, bool isPublic, AppDbContext repo,
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        if (await repo.GetUserMediaUploadSizeLimitAsync(uid, token) < contents.ContentStream.Length)
            return Failure.TooLong;
        if ((await repo.UpdateMediaAsync(uid, name, contents, token)).ToNullable() is { } f)
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
    /// <param name="uid">author id</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>the result of creating, <see cref="Either"/> <see cref="Failure"/> or inserted slug name</returns>
    public static async Task<Either<Failure, string>> DoSubmitMediaCreationAsync(string filename, Object mEntry,
        Guid uid, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        if (await repo.GetUserMediaUploadSizeLimitAsync(uid, token) < mEntry.ContentStream.Length)
            return Failure.TooLong;
        filename = SlugifyFilename(filename);
        RoutingLogging.LogSubmitNew_ForNameWithUidAndPublic(logger, filename, uid);
        var insertStatus = await repo.CreateMediaEntryAsync(uid, filename, mEntry, token);
        RoutingLogging.LogSubmitNew_InsertResultByStatus(logger, insertStatus);
        var insertedName = default(string)!;
        var failCode = default(Failure);
        insertStatus.Match(
            inserted => insertedName = inserted,
            f => failCode = f
        );
        if (failCode != default)
            return failCode;
        await _clearCacheEntriesAsync(cache, logger, insertedName, token);
        // we don't invalidate the listing caches because the insert won't cause the cached snapshot to become invalid
        // (unlike temporal or permissions update)
        return insertedName;
    }

    /// <summary>
    /// Renders <see cref="IManageCommand.Stats"/> for a post.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="uid">accessor id (must have write permissions)</param>
    /// <param name="perms">medium's current permissions (to be supplied by caller)</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>the <see cref="IManageCommand.Stats"/> for the post referenced by slug</returns>
    /// <exception cref="InvalidOperationException">if there was an internal error due to missing middleware filtering</exception>
    public static async Task<IManageCommand.Stats> DoGetManagePageForNameAndPermissionAsync(
        string name, Guid uid, IManageCommand.Permissions perms, AppDbContext repo, IFusionCache cache, 
        CancellationToken token)
    {
        // todo: caching
        var meta = await repo.GetMetadataForMediaAsync(uid, name, token);
        if (meta is null)
            throw new InvalidOperationException(
                "the require write permission middleware did not catch a missing entry");

        return new IManageCommand.Stats
        {
            ContentType = meta.Value.ContentType,
            Size = meta.Value.Size,
            Permissions = perms
        };
    }

    /// <summary>
    /// Submits a rename for a medium.
    /// </summary>
    /// <param name="name">(old) slug name</param>
    /// <param name="uid">author id</param>
    /// <param name="renameCommand">rename destination details</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>
    ///     the result of renaming with duplicate slug resolution,
    ///     <see cref="Either"/> <see cref="Failure"/> or new slug name
    /// </returns>
    public static async Task<Either<Failure, string>> DoSubmitRenameForNameAsync(
        string name, Guid uid, IManageCommand.Rename renameCommand, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        var newSlug = Contents.ComputeSlugName(renameCommand.RenameTo);
        RoutingLogging.LogSubmitManage_RenameBySlug(logger, name, uid, newSlug);
        var renameResult = await repo.RenameMediaSlugAsync(uid, name, newSlug, token);
        RoutingLogging.LogSubmitManage_RenameResultByStatus(logger, renameResult);
        
        if (renameResult.IsRight)
            // invalidate cache entries related to old name
            await Task.WhenAll(
                    ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache,
                        ContentAccessFilterConfig, "manager:rename", token),
                    _clearCacheEntriesAsync(cache, logger, name, token));
        return renameResult;
    }

    /// <summary>
    /// Submits a change of permissions for a medium.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="uid">author id</param>
    /// <param name="permissionsCommand">new permissions</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoSubmitChangePermissionsForNameAsync(
        string name, Guid uid, IManageCommand.SetPermissions permissionsCommand, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        var newPerms = permissionsCommand.Permissions;
        RoutingLogging.LogSubmitManage_ChangePermissionsBySlug(logger, name, uid, newPerms);
        var changePermissionsResult = await repo.UpdateMediaPermissionsAsync(uid, name, newPerms, token);
        RoutingLogging.LogSubmitManage_ChangePermissionResultByStatus(logger, changePermissionsResult);
        
        if (changePermissionsResult.IsNone)
        {
            if (!newPerms.Public)
            {
                await Task.WhenAll(
                    cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, newPerms.Public), token: token)
                        .AsTask(),
                    ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache,
                        ContentAccessFilterConfig, "manager:chperm -public", token)
                );
            }
        }
        return changePermissionsResult;
    }
   
    /// <summary>
    /// Submits a change of author for a medium.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="uid">author id</param>
    /// <param name="isPublic">true if the post has anonymous read/listable permissions</param>
    /// <param name="authorCommand">new author details</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>
    ///     the result of changing author,
    ///     <see cref="Either"/> <see cref="Failure"/> or new author's <see cref="Guid"/>
    /// </returns>
    public static async Task<Either<Failure, Guid>> DoSubmitSetAuthorForNameAsync(
        string name, Guid uid, bool isPublic, IManageCommand.SetAuthor authorCommand, AppDbContext repo,
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var newAuthor = authorCommand.NewAuthor;
        RoutingLogging.LogSubmitManage_ChangeAuthorBySlug(logger, name, uid, newAuthor);
        var changeAuthorResult = await repo.UpdateMediaAuthorAsync(uid, name, newAuthor, token);
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
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoDeleteMediumAsync(
        string name, bool isPublic, Guid uid, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, 
        CancellationToken token)
    {
        RoutingLogging.LogSubmitManage_ExecuteDeleteForSlug(logger, name, uid);
        var execDeleteResult = await repo.DeleteMediaAsync(uid, name, token);
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
                _clearCacheEntriesAsync(cache, logger, name, token)
            );
            return default;
        });
        return execDeleteResult;
    }
    
    // the HtmlApi and JsonApi versions differ only in terms of output rendering and have the same logic in the middle
    // so wrap the unified path in a function and capture the authentication-related extractor
    private static Func<ClaimsPrincipal, AppDbContext, IFusionCache, CancellationToken, int, string?, Task<TR>> 
    ExtractUidFromClaimsThenInvokeGetAllAvailableMediaThenTransformResult<TR>(
        Func<IEnumerable<Entry>, TR> renderer)
        => async (ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, CancellationToken token,
                // suppress CS9099 because ASP.NET's reflection scans the lambda type not the delegate type for binding
                // and optionals
                #pragma warning disable CS9099
                [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null) =>
                #pragma warning restore CS9099
        {
            var uidFromAuth = auth.RequireUid;
            var date = beforeOrAt is null
                ? DateTime.UtcNow
                : DateTime.Parse(beforeOrAt, null, DateTimeStyles.RoundtripKind);

            var listing = await DoGetAllAvailableMediaEntriesForUserAsync(uidFromAuth, limit, date, repo, cache, token);
            return renderer(listing);
        };
        
    /// <summary>
    /// Lists the media entries owned by the given user. 
    /// </summary>
    /// <param name="uid">user id of listing accessor</param>
    /// <param name="limit">(pagination) maximum number of posts</param>
    /// <param name="beforeOrAtUtc">(pagination) timestamp to not query more recent than</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a List of <see cref="Entry"/></returns>
    public static async Task<IEnumerable<Entry>> DoGetAllAvailableMediaEntriesForUserAsync(
        Guid uid, int limit, DateTime beforeOrAtUtc, AppDbContext repo, IFusionCache cache, CancellationToken token)
    {
        var listing = await cache.GetOrSetAsync(CacheHelpers.ListingKey(uid, beforeOrAtUtc, limit),
            _ => repo.GetAllMediaForOwnerAsync(uid, beforeOrAtUtc, limit, token),
            tags: CacheHelpers.ListingTags(uid, false), token: token);
        return listing;
    }
    
    private static async Task _clearCacheEntriesAsync(IFusionCache cache, ILogger<Routing> logger, string name,
        CancellationToken token)
    {
        RoutingLogging.LogMediaCacher_ClearForSlug(logger, name);
        // TODO: caching
    }

    internal static string SlugifyFilename(string filename)
    {
        var ext = filename;
        var dot = filename.LastIndexOf('.');

        if (dot != -1 && filename.Length > dot + 1)
        {
            ext = filename[(dot + 1)..].ToLower();
            filename = filename[..dot];
        }
        else
            ext = "";

        filename = Contents.ComputeSlugName(filename);
        if (ext.Length > 0)
        {
            ext = Contents.ComputeSlugName(ext);
            filename += '.' + ext;
        }

        return filename;
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
        Either<Failure, string> insertStatus);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: rename to {newName}")]
    internal static partial void LogSubmitManage_RenameBySlug(ILogger<Routing> logger,
        string name, Guid uid, string newName);

    [LoggerMessage(LogLevel.Debug, "rename result: {renameStatus}")]
    internal static partial void LogSubmitManage_RenameResultByStatus(ILogger<Routing> logger,
        Either<Failure, string> renameStatus);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: change permission to {newPerms}")]
    internal static partial void LogSubmitManage_ChangePermissionsBySlug(ILogger<Routing> logger,
        string name, Guid uid, IManageCommand.Permissions newPerms);
    
    [LoggerMessage(LogLevel.Debug, "change permission result: {status}")]
    internal static partial void LogSubmitManage_ChangePermissionResultByStatus(ILogger<Routing> logger,
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
