using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Blog;
using CsSsg.Src.Db;
using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddBlogRoutes(string apiPrefix)
        {
            app.AddBlogHtmlRoutes();
            app.AddBlogJsonRoutes(apiPrefix);
        }
    }

    private static class CacheHelpers
    {
        internal static string HtmlBodyKey(string name)
            => $"html.body/{name}";
        internal static string MarkdownContentsKey(string name)
            => $"md/{name}";

        internal static string ListingKey(Guid? uid, DateTime dateUtc, int limit)
        {
            Debug.Assert(dateUtc.ToUniversalTime() == dateUtc, "datetime is not in utc format");
            return $"listing/{uid};{dateUtc};{limit}";
        }
        
        internal static readonly string[] HtmlBodyTags = ["html"];
        internal static readonly string[] MarkdownContentTags = ["md"];
        internal static List<string> ListingTags(Guid? uid, bool isPublic)
        {
            List<string> tags = [];
            if (isPublic) tags.Add("listing");
            if (uid is not null) tags.Add($"listing/{uid}");
            return tags;
        }
    }
    
    /// <summary>
    /// Get the rendered HTML entry that can be consumed by views, if allowed.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="loggedInUid">logged in user (or <c>null</c>)</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>the rendered contents, otherwise <c>None</c> if unable</returns>
    public static async Task<Option<Contents>> DoGetRenderedBlogEntryForNameAsync(string name, Guid? loggedInUid, 
        AppDbContext repo, IFusionCache cache, CancellationToken token)
    => await cache.GetOrSetAsync(CacheHelpers.HtmlBodyKey(name), async _ =>
        {
            var contents = await _fetchMarkdownAsync(cache, repo, loggedInUid, name, token);
            return contents.Map(RenderHtml);
        }, tags: CacheHelpers.HtmlBodyTags, token: token);
    
    /// <summary>
    /// Commits an update to post contents.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="uid">committer id</param>
    /// <param name="cEntry">new contents</param>
    /// <param name="isPublic">whether the post is public (only affects cache invalidations)</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoSubmitBlogEntryEditForNameAsync(
        string name, Guid uid, Contents cEntry, bool isPublic, AppDbContext repo,
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        if ((await repo.UpdateContentAsync(uid, name, cEntry, token)).ToNullable() is { } f)
            return f;
        RoutingLogging.LogUpdater_CommitBySlugName(logger, name);
        await _clearCacheEntriesAsync(cache, logger, name, token);
        RoutingLogging.LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(logger, "updater", 
            name, uid, isPublic);
        await cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, isPublic), token: token);
        return Option<Failure>.None;
    }
    
    /// <summary>
    /// Creates a new post, resolving duplicate slug name if applicable. 
    /// </summary>
    /// <param name="cEntry">new contents</param>
    /// <param name="uid">author id</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>the result of creating, <see cref="Either"/> <see cref="Failure"/> or inserted slug name</returns>
    public static async Task<Either<Failure, string>> DoSubmitBlogEntryCreationAsync(Contents cEntry, Guid uid,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        RoutingLogging.LogSubmitNew_ForTitleWithUidAndPublic(logger, cEntry.Title, uid);
        var insertStatus = await repo.CreateContentAsync(uid, cEntry, token);
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
    /// <param name="perms">post's current permissions (to be supplied by caller)</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>the <see cref="IManageCommand.Stats"/> for the post referenced by slug</returns>
    /// <exception cref="InvalidOperationException">if there was an internal error due to missing middleware filtering</exception>
    public static async Task<IManageCommand.Stats> DoGetManagePageForNameAndPermissionAsync(
        string name, Guid uid, IManageCommand.Permissions perms, AppDbContext repo, IFusionCache cache, 
        CancellationToken token)
    {
        var articleResult = await _fetchMarkdownAsync(cache, repo, uid, name, token);
        if (articleResult.IsNone)
            throw new InvalidOperationException(
                "the require write permission middleware did not catch a missing entry");
        var article = articleResult.Value();

        return new IManageCommand.Stats
        {
            Title = article.Title,
            ContentLength = article.Body.Length,
            Permissions = perms
        };
    }

    /// <summary>
    /// Submits a rename for a post.
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
        var renameResult = await repo.UpdateSlugAsync(uid, name, newSlug, token);
        RoutingLogging.LogSubmitManage_RenameResultByStatus(logger, renameResult);
        
        if (renameResult.IsRight)
            // invalidate cache entries related to old name
            await Task.WhenAll(
                    ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache,
                        "manager:rename", token),
                    _clearCacheEntriesAsync(cache, logger, name, token));
        return renameResult;
    }

    /// <summary>
    /// Submits a change of permissions for a post.
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
        var changePermissionsResult = await repo.UpdatePermissionsAsync(uid, name, newPerms, token);
        RoutingLogging.LogSubmitManage_ChangePermissionResultByStatus(logger, changePermissionsResult);
        
        if (changePermissionsResult.IsNone)
        {
            if (!newPerms.Public)
            {
                await Task.WhenAll(
                    cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, newPerms.Public), token: token)
                        .AsTask(),
                    ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache,
                        "manager:chperm -public", token)
                );
            }
        }
        return changePermissionsResult;
    }
   
    /// <summary>
    /// Submits a change of author for a post.
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
        var changeAuthorResult = await repo.UpdateAuthorAsync(uid, name, newAuthor, token);
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
                        "manager:chauthor", token));
            }
        }
        return changeAuthorResult;
    }
    
    /// <summary>
    /// Submits a deletion request for a post.
    /// </summary>
    /// <param name="name">slug name</param>
    /// <param name="isPublic">true if the post has anonymous read/listable permissions</param>
    /// <param name="uid">author id</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="logger">routing class logger</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
    public static async Task<Option<Failure>> DoDeleteBlogEntryAsync(
        string name, bool isPublic, Guid uid, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, 
        CancellationToken token)
    {
        RoutingLogging.LogSubmitManage_ExecuteDeleteForSlug(logger, name, uid);
        var execDeleteResult = await repo.DeleteContentAsync(uid, name, token);
        RoutingLogging.LogSubmitManage_DeleteResultByStatus(logger, execDeleteResult);
        // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
        await execDeleteResult.IfNoneAsync(async () =>
        {
            RoutingLogging.LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(logger,
                "manager:chauthor", name, uid, false);
            await Task.WhenAll(
                cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, isPublic), token: token)
                    .AsTask(),
                ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache, "manager:delete", token),
                _clearCacheEntriesAsync(cache, logger, name, token)
            );
            return default;
        });
        return execDeleteResult;
    }
    
    /// <summary>
    /// Lists the content entries available for the given user. 
    /// </summary>
    /// <param name="uid">user id of listing accessor (null for anonymous)</param>
    /// <param name="limit">(pagination) maximum number of posts</param>
    /// <param name="beforeOrAtUtc">(pagination) timestamp to not query more recent than</param>
    /// <param name="repo">request's database context</param>
    /// <param name="cache">shared cache</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a List of <see cref="Entry"/></returns>
    public static async Task<IEnumerable<Entry>> DoGetAllAvailableBlogEntriesAsync(
        Guid? uid, int limit, DateTime beforeOrAtUtc, AppDbContext repo, IFusionCache cache, CancellationToken token)
    {
        var listing = await cache.GetOrSetAsync(CacheHelpers.ListingKey(uid, beforeOrAtUtc, limit),
            _ => repo.GetAvailableContentAsync(uid, beforeOrAtUtc, limit, token),
            tags: CacheHelpers.ListingTags(uid, true), token: token);
        return listing;
    }
    
    private static ValueTask<Option<Contents>> _fetchMarkdownAsync(IFusionCache cache, AppDbContext repo, Guid? userId,
        string? name, CancellationToken token)
    {
        if (name is null)
            return new(Option<Contents>.None);
        return cache.GetOrSetAsync(CacheHelpers.MarkdownContentsKey(name), async _ =>
        {
            var contents = await repo.GetContentAsync(userId, name, token);
            return contents.Match(
                Option<Contents>.Some, 
                /* Failure */ _ => Option<Contents>.None
            );
        }, tags: CacheHelpers.MarkdownContentTags, token: token);
    }

    private static async Task _clearCacheEntriesAsync(IFusionCache cache, ILogger<Routing> logger, string name,
        CancellationToken token)
    {
        RoutingLogging.LogContentCacher_ClearForSlug(logger, name);
        await cache.RemoveAsync(CacheHelpers.MarkdownContentsKey(name), token: token);
        await cache.RemoveAsync(CacheHelpers.MarkdownContentsKey(name), token: token);
    }
   
    extension(Contents contents)
    {
        private Contents RenderHtml() => contents with
        {
            Body = MarkdownHandler.RenderMarkdownToHtmlArticle(contents.Body)
        };
    }


}

internal static partial class RoutingLogging
{
    [LoggerMessage(LogLevel.Debug, "content cacher: set slug {name}")]
    internal static partial void LogContentCacher_SetForSlug(ILogger<Routing> logger, string name);
    
    [LoggerMessage(LogLevel.Debug, "content cacher: clear slug {name}")]
    internal static partial void LogContentCacher_ClearForSlug(ILogger<Routing> logger, string name);
    
    [LoggerMessage(LogLevel.Debug, "{context}: slug {name}: invalidate cache: uid={uid} public={isPublic}")]
    internal static partial void LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(ILogger<Routing> logger, 
        string context, string name, Guid uid, bool isPublic);
   
    [LoggerMessage(LogLevel.Information, "updater: commit slug {name}")]
    internal static partial void LogUpdater_CommitBySlugName(ILogger<Routing> logger, string name);

    [LoggerMessage(LogLevel.Information, "submit new: title {title} from {uid}")]
    internal static partial void LogSubmitNew_ForTitleWithUidAndPublic(ILogger<Routing> logger,
        string title, Guid uid);

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
