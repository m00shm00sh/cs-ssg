using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using KotlinScopeFunctions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Blog;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.Slices;
using CsSsg.Src.Slices.ViewModels;
using CsSsg.Src.User;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static class RoutingExtensions
{
    // also used by User.RoutingExtensions
    internal const string BLOG_PREFIX = "/blog";
    private const string RX_SLUG_WITH_OPT_UUID = @"^\w+(-\w+)*(\.[[0-9a-f]]{{32}})?$";
    [StringSyntax("Route")] private const string NAME_SLUG = $"/{{name:regex({RX_SLUG_WITH_OPT_UUID})}}";
    
    private const string EDIT_SUFFIX = "/edit";
    private const string SUBMIT_EDIT_SUFFIX = "/edit.1";
    private const string NEW_SLUG = "/-new";
    private const string SUBMIT_NEW_SLUG = "/-new.1";
    private const string MANAGE_SUFFIX = "/manage";
    private const string SUBMIT_MANAGE_SUFFIX = "/manage.1";
    
    private static string LinkForName(string? name)
        => $"{BLOG_PREFIX}/{name}";
    private static string EditLinkForName(string? name, string action = EDIT_SUFFIX)
        => LinkForName(name) + action;
    private static string ManageLinkForName(string name, string action = MANAGE_SUFFIX)
        => LinkForName(name) + action;
    
    extension(WebApplication app)
    {
        public void AddBlogRoutes()
        {
            app.MapGet(BLOG_PREFIX, GetAllAvailableBlogEntriesAsync);
            
            app.MapGet(BLOG_PREFIX + NAME_SLUG, GetBlogEntryForNameAsync)
                .AddEndpointFilter<ContentAccessPermissionFilter>();

            app.MapGet(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, GetBlogEntryEditorForNameAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, PostBlogEntryEditorForNameAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_EDIT_SUFFIX, SubmitBlogEntryEditForNameAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapGet(BLOG_PREFIX + NEW_SLUG, GetBlogEntryCreatorAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
                
            app.MapPost(BLOG_PREFIX + NEW_SLUG, PostBlogEntryCreatorAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
            
            app.MapPost(BLOG_PREFIX + SUBMIT_NEW_SLUG, SubmitBlogEntryCreationAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapGet(BLOG_PREFIX + NAME_SLUG + MANAGE_SUFFIX, GetManagePageForNameAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_MANAGE_SUFFIX, SubmitManagePageForNameAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapGet("/", () => Results.Redirect(BLOG_PREFIX));
            app.MapGet("/contact", () => Results.Redirect(LinkForName("contact")));
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


    private static async Task<Results<RazorSliceHttpResult<BlogEntry>, NotFound>>
    GetBlogEntryForNameAsync(string name, HttpContext ctx, ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache,
        CancellationToken token)
    {
        var uidFromCookie = auth?.TryUid;
        var contents = await cache.GetOrSetAsync(CacheHelpers.HtmlBodyKey(name), async _ =>
        {
            var contents = await _fetchMarkdownAsync(cache, repo, uidFromCookie, name, token);
            return contents.Map(RenderHtml);
        }, tags: CacheHelpers.HtmlBodyTags, token: token);
        var hasWritePermission = ctx.Features.Get<PostPermission>()?.AccessLevel.IsWrite is not null;

        var editPage = hasWritePermission ? EditLinkForName(name) : null;
        // unwrap from monad to nullable so that we get the desired type inference
        return contents.ToNullable() is var (title, article)
            ? Results.Extensions.RazorSlice<BlogEntryView, BlogEntry>(
                new BlogEntry(title, new HtmlString(article), editPage))
            : TypedResults.NotFound();
    }

    private static Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>>
    GetBlogEntryEditorForNameAsync(string name, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo,
        IFusionCache cache, IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        return RenderEditPageAsync(name, uidFromCookie, null, repo, cache, aft, token);
    }
    
    private static Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>>
    PostBlogEntryEditorForNameAsync(string name, [FromForm] EditorFormContents contents, HttpContext ctx,
    ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af,
    CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetTokens(ctx);
        return RenderEditPageAsync(name, uidFromCookie, contents, repo, cache, aft, token);
    }

    private static Task<IResult> SubmitBlogEntryEditForNameAsync(
        string name, [FromForm] EditorFormContents contents, HttpContext ctx, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var isPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        return DoSubmitBlogEntryEditForNameAsync(name, uidFromCookie, contents, isPublic, repo, cache,
            logger, token);
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
    
    private static async Task<RazorSliceHttpResult<BlogEntryEdit>>
    GetBlogEntryCreatorAsync(HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var page = await RenderEditPageAsync(null, uidFromCookie, null, repo, cache, aft, token);
        return (RazorSliceHttpResult<BlogEntryEdit>)page.Result;
    }
    
    private static async Task<RazorSliceHttpResult<BlogEntryEdit>>
    PostBlogEntryCreatorAsync([FromForm] EditorFormContents contents, HttpContext ctx, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache, IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetTokens(ctx);
        var page = await RenderEditPageAsync(null, uidFromCookie, contents, repo, cache, aft, token);
        return (RazorSliceHttpResult<BlogEntryEdit>)page.Result;
    }

    private static Task<IResult> SubmitBlogEntryCreationAsync(
        [FromForm] EditorFormContents content, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo,
        IFusionCache cache, IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        return DoSubmitBlogEntryCreationAsync(content, uidFromCookie, repo, cache, logger,
            token);
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

    private static Task<Results<BadRequest<string>, RazorSliceHttpResult<ManageEntry>>>
    GetManagePageForNameAsync(string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var initiallyPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        return DoGetManagePageForNameAsync(name, uidFromCookie, initiallyPublic, repo, cache, aft, token);
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

    private static Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitManagePageForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx,
        AppDbContext repo, IFusionCache cache, IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var initiallyPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var contentFilter = ctx.Features.Get<ContentAccessPermissionFilter>()
            ?? throw new InvalidOperationException("couldn't find content filter instance"); 
        var formParseResult = ManageCommand.FromForm(form);
        return formParseResult.MatchAsync(
            argEx => Task.FromResult(Results.BadRequest(argEx.Message)),
            command => DoSubmitManagePageForNameAsync(name, uidFromCookie, initiallyPublic, command,
                contentFilter, repo, cache, logger, token)
        );
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

    private static Task<RazorSliceHttpResult<Listing>> GetAllAvailableBlogEntriesAsync(
        ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache, CancellationToken token,
        [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null)
    {
        var date = beforeOrAt is null
            ? DateTime.UtcNow
            : DateTime.Parse(beforeOrAt, null, DateTimeStyles.RoundtripKind);
        return DoGetAllAvailableBlogEntriesAsync(auth.TryUid, limit, date, repo, cache, token);
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

internal partial class ContentAccessPermissionFilter(
    ILogger<ContentAccessPermissionFilter> logger, IFusionCache cache, AppDbContext repo)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var uid = http.User.TryUid;
        if (http.GetRouteValue("name") is not string name)
            throw new InvalidOperationException("unexpected: could not find route param \"name\" having type string");
        var token = http.RequestAborted;

        LogContentAccessPermissionsNameUid(logger, name, uid);
        var canAccess = await cache.GetOrSetAsync(
            $"access/{uid}/{name}",
            async _ => await repo.GetPermissionsForContentAsync(uid, name, token),
            tags: ["access"], token: token);
        LogContentAccessPermissionsCompletedNameUid(logger, name, uid, canAccess);
        if (canAccess is null)
            return Results.NotFound();
        UnexpectedEnumValueException.VerifyOrThrow(canAccess);
        http.Features.Set(new PostPermission(canAccess.Value));
        http.Features.Set(this);
        return await next(context);
    }
    
    public async Task InvalidateAccessCacheAsync(string context, CancellationToken token, 
        ICollection<string>? extraKeys = null)
    {
        extraKeys ??= Array.Empty<string>();
        LogInvalidateAccessCaches(logger, context, extraKeys);
        await cache.RemoveByTagAsync(["access", ..extraKeys], token: token);
    }


    [LoggerMessage(LogLevel.Information, 
        "content access permissions: lookup: name={name}, uid={uid}")]
    static partial void LogContentAccessPermissionsNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid);
    [LoggerMessage(LogLevel.Information, 
        "content access permissions: lookup: name={name}, uid={uid}, permissions={perms}")]
    static partial void LogContentAccessPermissionsCompletedNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid, AccessLevel? perms);    
    
    [LoggerMessage(LogLevel.Information, "{context}: invalidate access caches; ek={extraKeys}")]
    static partial void LogInvalidateAccessCaches(ILogger<ContentAccessPermissionFilter> logger,
        string context, IEnumerable<string> extraKeys);

}

internal partial class WritePermissionFilter(
    ILogger<WritePermissionFilter> logger, AppDbContext repo)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var uid = http.User.RequireUid;
        var permission = http.Features.Get<PostPermission>()?.AccessLevel;
        var updateSlug = http.GetRouteValue("name") as string;
        if (updateSlug is null && permission.HasValue)
            throw new InvalidOperationException(
                "unexpected: could not find route param \"name\" but we have existing permissions");
        var token = http.RequestAborted;
        var canCreate = permission is null && await repo.DoesUserHaveCreatePermissionAsync(uid, token);
        
        LogWritePermissionsInvocation(logger, updateSlug, uid, permission, canCreate);
        
        return permission switch
        {
            null when !canCreate =>
                Results.NotFound(),
            AccessLevel.None or AccessLevel.Read =>
                Results.Forbid(),
            null when canCreate =>
                await next(context),
            AccessLevel.Write or AccessLevel.WritePublic =>
                await next(context),
            _ => throw UnexpectedEnumValueException.Create(permission)
        };
    }

    [LoggerMessage(LogLevel.Information, 
        "write access permissions: name={name}, uid={uid}, perm={perm} canCreate={canCreate}")]
    static partial void LogWritePermissionsInvocation(ILogger<WritePermissionFilter> logger,
        string? name, Guid uid, AccessLevel? perm, bool canCreate);
}

// class instead of readonly struct so that it can be nullable
file record PostPermission(AccessLevel AccessLevel);