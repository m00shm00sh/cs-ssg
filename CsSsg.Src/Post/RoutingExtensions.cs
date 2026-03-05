using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using KotlinScopeFunctions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Blog;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.Slices;
using CsSsg.Src.Slices.ViewModels;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddBlogRoutes()
        {
            app.AddBlogHtmlRoutes();
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

    // NOTE: isPublic is used here only to determine cache invalidation tag; it does not commit any modifications to DB
    public static async Task<IResult> DoSubmitBlogEntryEditForNameAsync(
        string name, Guid uid, Contents cEntry, bool isPublic, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        if ((await repo.UpdateContentAsync(uid, name, cEntry, token)).ToNullable() is { } f) return f.AsResult;
        var article = MarkdownHandler.RenderMarkdownToHtmlArticle(cEntry.Body);
        RoutingLogging.LogUpdater_CommitBySlugName(logger, name);
        await _setCacheEntriesAsync(cache, logger, name, cEntry, article, token);
        RoutingLogging.LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(logger, "updater", 
            name, uid, isPublic);
        await cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, isPublic), token: token);
        return TypedResults.Redirect(LinkForName(name));
    }
    
    public static async Task<IResult> DoSubmitBlogEntryCreationAsync(Contents cEntry, Guid uid, AppDbContext repo,
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
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
            return failCode.AsResult;
        var article = MarkdownHandler.RenderMarkdownToHtmlArticle(cEntry.Body);
        await _setCacheEntriesAsync(cache, logger, insertedName, cEntry, article, token);
        // we don't invalidate the caches because the insert won't cause the cached snapshot to become invalid
        // (unlike temporal or permissions update)
        return TypedResults.Redirect(LinkForName(insertedName));
    }

    public static async Task<Results<BadRequest<string>, RazorSliceHttpResult<ManageEntry>>>
    DoGetManagePageForNameAsync(string name, Guid uid, bool initiallyPublic, AppDbContext repo, IFusionCache cache,
        AntiforgeryTokenSet aft, CancellationToken token)
    {
        var articleResult = await _fetchMarkdownAsync(cache, repo, uid, name, token);
        if (articleResult.IsNone)
            throw new InvalidOperationException(
                "the require write permission middleware did not catch a missing entry");
        var article = articleResult.Value();

        return Results.Extensions.RazorSlice<ManageEntryView, ManageEntry>(
            new ManageEntry(name, article.Title, article.Body.Length, ManageLinkForName(name, SUBMIT_MANAGE_SUFFIX),
                initiallyPublic, aft));
    }

    public static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> DoSubmitManagePageForNameAsync(
        string name, Guid uid, bool initiallyPublic, ManageCommand manageCommand,
        ContentAccessPermissionFilter contentFilter, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
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
                            contentFilter.InvalidateAccessCacheAsync("manager:rename", token),
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
                                contentFilter.InvalidateAccessCacheAsync("manager:chperm -public", token)
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
                                contentFilter.InvalidateAccessCacheAsync("manager:chauthor", token)
                            );
                        }
                        return Results.Redirect(BLOG_PREFIX);
                    });
            case ManageCommand.ActiveCommand.Delete:
                RoutingLogging.LogSubmitManage_ExecuteDeleteForSlug(logger, name, uid);
                var execDeleteResult = await repo.DeleteContentAsync(uid, name, token);
                RoutingLogging.LogSubmitManage_DeleteResultByStatus(logger, execDeleteResult);
                return await execDeleteResult.MatchAsync(
                    failCode => failCode.AsResult,
                    async () =>
                    {
                            RoutingLogging.LogUpdaterOrManager_SlugNameInvalidateCachesByUidAndPublic(logger,
                                "manager:chauthor", name, uid, false);
                            await Task.WhenAll(
                                cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, initiallyPublic), token: token)
                                    .AsTask(),
                                contentFilter.InvalidateAccessCacheAsync("manager:delete", token),
                                _clearCacheEntriesAsync(cache, logger, name, token)
                            );
                        return Results.Redirect(BLOG_PREFIX);
                    });
            default:
                throw UnexpectedEnumValueException.Create(activeCommand.Case as ManageCommand.ActiveCommand?);
        }
    }

    public static async Task<RazorSliceHttpResult<Listing>> DoGetAllAvailableBlogEntriesAsync(
        Guid? uid, int limit, DateTime beforeOrAtUtc, AppDbContext repo, IFusionCache cache, CancellationToken token)
    {
        var listing = await cache.GetOrSetAsync(CacheHelpers.ListingKey(uid, beforeOrAtUtc, limit),
            _ => repo.GetAvailableContentAsync(uid, beforeOrAtUtc, limit, token),
            tags: CacheHelpers.ListingTags(uid, true), token: token);
        return Results.Extensions.RazorSlice<BlogListing, Listing>(
            new Listing(listing.Select(e =>
                new ListingEntry(e.Title, LinkForName(e.Slug),
                    e.AuthorHandle, e.IsPublic, e.LastModified,
                    ManageLinkForName(e.Slug).TakeIf(_ => e.AccessLevel.IsWrite)
                )),
            CanModify: uid is not null,
            ToNewPostPage: uid?.Let(_ => LinkForName(NEW_SLUG[1..]))
        ));
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

    private static async Task _setCacheEntriesAsync(IFusionCache cache, ILogger<Routing> logger, string name,
        Contents contents, string markdownHtml, CancellationToken token)
    {
        RoutingLogging.LogContentCacher_SetForSlug(logger, name);
        var opt = Option<Contents>.Some;
        await Task.WhenAll(
            cache.SetAsync(CacheHelpers.HtmlBodyKey(name), opt(contents with { Body = markdownHtml }),
                tags: CacheHelpers.HtmlBodyTags, token: token).AsTask(),
            cache.SetAsync(CacheHelpers.MarkdownContentsKey(name), opt(contents),
                tags: CacheHelpers.MarkdownContentTags, token: token).AsTask()
        );
    }

    private static async Task _clearCacheEntriesAsync(IFusionCache cache, ILogger<Routing> logger, string name,
        CancellationToken token)
    {
        RoutingLogging.LogContentCacher_ClearForSlug(logger, name);
        await cache.RemoveAsync(CacheHelpers.MarkdownContentsKey(name), token: token);
        await cache.RemoveAsync(CacheHelpers.MarkdownContentsKey(name), token: token);
    }

    // unify the handling for both GET and POST:
    // if both formTitle and formContents are null then GET endpoint was matched and we fetch from cache;
    // if neither are null then POST was matched and use contents. The handler lambda is responsible for CSRF validation
    // When nameSlug is null, then we are rendering the edit for the create page.
    public static async Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>> RenderEditPageAsync(
        string? nameSlug, Guid userId, Contents? formData, AppDbContext repo, IFusionCache cache,
        AntiforgeryTokenSet aft, CancellationToken token)
    {
        var contents = formData ?? await _fetchMarkdownAsync(cache, repo, userId, nameSlug, token);
        var isCreatePage = nameSlug is null;
        
        if (contents.IsNone && !isCreatePage)
            return TypedResults.NotFound();
        // edit page for create; compute name slug
        if (contents.IsSome && isCreatePage)
            nameSlug = contents.Map(c => c.ComputeSlugName()).ValueUnsafe();
        
        var htmlContents = contents.Map(c => c.RenderHtml()).ToNullable() ?? default;
        var toPreviewPage = LinkForName(NEW_SLUG[1..]);
        var toSubmitPage = LinkForName(SUBMIT_NEW_SLUG[1..]);
        if (!isCreatePage)
        {
            toPreviewPage = EditLinkForName(nameSlug);
            toSubmitPage = EditLinkForName(nameSlug, SUBMIT_EDIT_SUFFIX);
        }

        return Results.Extensions.RazorSlice<BlogEntryEditView, BlogEntryEdit>(
            new BlogEntryEdit(new HtmlString(htmlContents.Body), contents.ToNullable(), 
                toPreviewPage, toSubmitPage, aft,
                isCreatePage ? nameSlug: null, 
                IsNewPost: true));
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
