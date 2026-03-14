using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Blog;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
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

    
    public static async Task<Option<Contents>> DoGetBlogEntryForNameAsync(string name, Guid? loggedInUid, 
        AppDbContext repo, IFusionCache cache, CancellationToken token)
    {
        var contents = await cache.GetOrSetAsync(CacheHelpers.HtmlBodyKey(name), async _ =>
        {
            var contents = await _fetchMarkdownAsync(cache, repo, loggedInUid, name, token);
            return contents.Map(RenderHtml);
        }, tags: CacheHelpers.HtmlBodyTags, token: token);

        // unwrap from monad to nullable so that we get the desired type inference
        return contents;
    }
    
    // NOTE: isPublic is used here only to determine cache invalidation tag; it does not commit any modifications to DB
    public static async Task<Option<Failure>> DoSubmitBlogEntryEditForNameAsync(
        string name, Guid uid, Contents cEntry, bool isPublic, bool isComingFromForm, AppDbContext repo,
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
    
    public static async Task<Either<string, Failure>> DoSubmitBlogEntryCreationAsync(Contents cEntry, Guid uid,
        bool isComingFromForm, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        RoutingLogging.LogSubmitNew_ForTitleWithUidAndPublic(logger, cEntry.Title, uid);
        var insertStatus = await repo.CreateContentAsync(uid, cEntry, token);
        RoutingLogging.LogSubmitNew_InsertResultByStatus(logger, insertStatus);
        var insertedName = default(string)!;
        var failCode = default(Failure);
        insertStatus.Match(
            (Failure f) => failCode = f,
            (string inserted) => insertedName = inserted
        );
        if (failCode != default)
            return failCode;
        await _clearCacheEntriesAsync(cache, logger, insertedName, token);
        // we don't invalidate the listing caches because the insert won't cause the cached snapshot to become invalid
        // (unlike temporal or permissions update)
        return insertedName;
    }

    public static async Task<ManageCommand.Stats> DoGetManagePageForNameAsync(
        string name, Guid uid, ManageCommand.Permissions perms, AppDbContext repo, IFusionCache cache, 
        CancellationToken token)
    {
        var articleResult = await _fetchMarkdownAsync(cache, repo, uid, name, token);
        if (articleResult.IsNone)
            throw new InvalidOperationException(
                "the require write permission middleware did not catch a missing entry");
        var article = articleResult.Value();

        return new ManageCommand.Stats
        {
            Title = article.Title,
            ContentLength = article.Body.Length,
            Permissions = perms
        };
    }

    public static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> DoSubmitManageEntryPageForNameAsync(
        string name, Guid uid, bool initiallyPublic, ManageCommand manageCommand, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        var activeCommand = manageCommand.GetActiveCommand();
        switch (activeCommand.Case)
        {
            case ArgumentException ex:
                return Results.BadRequest(ex.Message);
            case ManageCommand.ActiveCommand.RenameTo:
                var newSlug = Contents.ComputeSlugName(manageCommand.RenameTo);
                RoutingLogging.LogSubmitManage_RenameBySlug(logger, name, uid, newSlug);
                var renameResult = await repo.UpdateSlugAsync(uid, name, newSlug, token);
                RoutingLogging.LogSubmitManage_RenameResultByStatus(logger, renameResult);
                return await renameResult.MatchAsync(
                    failCode => failCode.AsResult,
                    async newName =>
                    {
                        // invalidate cache entries related to old name
                        await Task.WhenAll(
                            ContentAccessPermissionFilter.InvalidateAccessCacheAsync(logger, cache, 
                                "manager:rename", token),
                            _clearCacheEntriesAsync(cache, logger, name, token)
                        );
                        return Results.Redirect(LinkForName(newName));
                    });
            case ManageCommand.ActiveCommand.NewPermissions:
                var newPerms = manageCommand.NewPermissions!.Value;
                RoutingLogging.LogSubmitManage_ChangePermissionsBySlug(logger, name, uid, newPerms);
                var changePermissionsResult = await repo.UpdatePermissionsAsync(uid, name, newPerms, token);
                RoutingLogging.LogSubmitManage_ChangePermissionResultByStatus(logger, changePermissionsResult);
                return await changePermissionsResult.MatchAsync(
                    failCode => failCode.AsResult,
                    async () =>
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
                        return Results.Redirect(BLOG_PREFIX);
                    });
            case ManageCommand.ActiveCommand.NewAuthor:
                var newAuthor = manageCommand.ReassignAuthorTo;
                RoutingLogging.LogSubmitManage_ChangeAuthorBySlug(logger, name, uid, newAuthor);
                var changeAuthorResult = await repo.UpdateAuthorAsync(uid, name, newAuthor, token);
                RoutingLogging.LogSubmitManage_ChangeAuthorResultByStatus(logger, changeAuthorResult);
                return await changeAuthorResult.MatchAsync(
                    failCode => failCode.AsResult,
                    async newAuthorId =>
                    {
                        // we only need to invalidate the perms and listing caches if author changes for private post
                        if (!initiallyPublic)
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
                                    "manager:chauthor", token)
                            );
                        }
                        return Results.Redirect(BLOG_PREFIX);
                    });
            case ManageCommand.ActiveCommand.Delete:
                return await DoDeleteBlogEntryAsync(name, initiallyPublic, uid, logger, repo, cache, token).Match(
                    failCode => failCode.AsResult,
                    () => Results.Redirect(BLOG_PREFIX)
                );
            default:
                throw UnexpectedEnumValueException.Create(activeCommand.Case as ManageCommand.ActiveCommand?);
        }
    }

    public static async Task<Option<Failure>> DoDeleteBlogEntryAsync(
        string name, bool isPublic, Guid uid, ILogger<Routing> logger, AppDbContext repo, IFusionCache cache,
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
                (Failure _) => Option<Contents>.None,
                Option<Contents>.Some
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

    extension(Failure f)
    {
        private IResult AsResult => f switch
        {
            Failure.NotFound =>
                Results.NotFound(),
            Failure.NotPermitted =>
                Results.Forbid(),
            Failure.Conflict or
            Failure.TooLong => 
                // a Results.UnprocessableEntity would also do here since it's a validation failure
                Results.BadRequest(),
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, null)
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
        Either<string, Failure> insertStatus);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: rename to {newName}")]
    internal static partial void LogSubmitManage_RenameBySlug(ILogger<Routing> logger,
        string name, Guid uid, string newName);

    [LoggerMessage(LogLevel.Debug, "rename result: {renameStatus}")]
    internal static partial void LogSubmitManage_RenameResultByStatus(ILogger<Routing> logger,
        Either<string, Failure> renameStatus);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: change permission to {newPerms}")]
    internal static partial void LogSubmitManage_ChangePermissionsBySlug(ILogger<Routing> logger,
        string name, Guid uid, ManageCommand.Permissions newPerms);
    
    [LoggerMessage(LogLevel.Debug, "change permission result: {status}")]
    internal static partial void LogSubmitManage_ChangePermissionResultByStatus(ILogger<Routing> logger,
        Option<Failure> status);

    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: change owner to email={newAuthor}")]
    internal static partial void LogSubmitManage_ChangeAuthorBySlug(ILogger<Routing> logger,
        string name, Guid uid, string newAuthor);
    
    [LoggerMessage(LogLevel.Debug, "change author result: {authorResult}")]
    internal static partial void LogSubmitManage_ChangeAuthorResultByStatus(ILogger<Routing> logger,
        Either<Guid, Failure> authorResult);
    
    [LoggerMessage(LogLevel.Information, "manager: slug {name}: uid={uid}: execute delete")]
    internal static partial void LogSubmitManage_ExecuteDeleteForSlug(ILogger<Routing> logger, string name, Guid uid);
    
    [LoggerMessage(LogLevel.Debug, "delete result: {status}")]
    internal static partial void LogSubmitManage_DeleteResultByStatus(ILogger<Routing> logger, Option<Failure> status);
}

internal abstract class Routing;
