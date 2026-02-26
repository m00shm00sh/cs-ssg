using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
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
using OneOf;

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
    
    extension(WebApplication app)
    {
        public void AddBlogRoutes()
        {
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

            app.MapGet(BLOG_PREFIX, GetAllAvailableBlogEntriesAsync);

            app.MapGet("/", () => Results.Redirect(BLOG_PREFIX));
            app.MapGet("/contact", () => Results.Redirect($"{BLOG_PREFIX}/contact"));
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
            return contents?.RenderHtml();
        }, tags: CacheHelpers.HtmlBodyTags, token: token);
        var hasWritePermission = ctx.Features.Get<PostPermission>()?.AccessLevel.IsWrite is not null;

        var editPage = hasWritePermission ? $"{BLOG_PREFIX}/{name}{EDIT_SUFFIX}" : null;
        return contents is var (title, article)
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
        return RenderEditPageAsync(name, uidFromCookie, false, null, repo, cache, aft, token);
    }
    
    private static Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>>
    PostBlogEntryEditorForNameAsync(string name, [FromForm] EditorFormContents contents, HttpContext ctx,
    ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af,
    CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetTokens(ctx);
        return RenderEditPageAsync(name, uidFromCookie, false, contents, repo, cache, aft, token);
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

    public static async Task<IResult> DoSubmitBlogEntryEditForNameAsync(
        string name, Guid uid, Contents cEntry, bool isPublic, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        if (await repo.UpdateContentAsync(uid, name, cEntry, token) is { } f) return f.AsResult;
        var article = MarkdownHandler.RenderMarkdownToHtmlArticle(cEntry.Body);
        RoutingLogging.LogUpdater_CommitBySlugName(logger, name);
        await _setCacheEntriesAsync(cache, name, cEntry, article, token);
        RoutingLogging.LogUpdater_SlugNameInvalidateCachesByUidAndPublic(logger, name, uid, isPublic);
        await cache.RemoveByTagAsync(CacheHelpers.ListingTags(uid, isPublic), token: token);
        return TypedResults.Redirect(BLOG_PREFIX + $"/{name}");
    }
    
    private static async Task<RazorSliceHttpResult<BlogEntryEdit>>
    GetBlogEntryCreatorAsync(HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var page = await RenderEditPageAsync(null, uidFromCookie, false, null, repo, cache, aft, token);
        return (RazorSliceHttpResult<BlogEntryEdit>)page.Result;
    }
    
    private static async Task<RazorSliceHttpResult<BlogEntryEdit>>
    PostBlogEntryCreatorAsync([FromForm] EditorFormContents contents, [FromForm] string initiallyPublic,
        HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af,
        CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetTokens(ctx);
        var isInitiallyPublic = initiallyPublic.ToLowerInvariant() == "on";
        var page = await RenderEditPageAsync(null, uidFromCookie, isInitiallyPublic, contents, repo, cache, aft, token);
        return (RazorSliceHttpResult<BlogEntryEdit>)page.Result;
    }

    private static Task<IResult> SubmitBlogEntryCreationAsync(
        [FromForm] EditorFormContents content, [FromForm] string? initiallyPublic, HttpContext ctx,
        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var isInitiallyPublic = initiallyPublic == "on";
        return DoSubmitBlogEntryCreationAsync(content, isInitiallyPublic, uidFromCookie, repo, cache, logger,
            token);
    }

    public static async Task<IResult> DoSubmitBlogEntryCreationAsync(
        Contents cEntry, bool isInitiallyPublic, Guid uid, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        RoutingLogging.LogSubmitNew_ForTitleWithUidAndPublic(logger, cEntry.Title, uid, isInitiallyPublic);
        var insertStatus = await repo.CreateContentAsync(uid, cEntry, token);
        RoutingLogging.LogSubmitNew_InsertResultByStatus(logger, insertStatus);
        var insertedName = default(string)!;
        var failCode = default(Failure);
        insertStatus.Switch(
            (string inserted) => insertedName = inserted,
            (Failure f) => failCode = f
        );
        if (failCode != default)
            return failCode.AsResult;
        var article = MarkdownHandler.RenderMarkdownToHtmlArticle(cEntry.Body);
        await _setCacheEntriesAsync(cache, insertedName, cEntry, article, token);
        // we don't invalidate the caches because the insert won't cause the cached snapshot to become invalid
        // (unlike temporal or permissions update)
        return TypedResults.Redirect(BLOG_PREFIX + $"/{insertedName}");
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
                new ListingEntry(e.Title, $"{BLOG_PREFIX}/{e.Slug}", e.LastModified,
                    CanDeleteOrMove: e.AccessLevel == AccessLevel.Write
                )),
            CanModify: uid is not null,
            ToNewPostPage: uid is not null ? BLOG_PREFIX + NEW_SLUG : null) 
        );
    }

    private static ValueTask<Contents?> _fetchMarkdownAsync(IFusionCache cache, AppDbContext repo, Guid? userId,
        string? name, CancellationToken token)
    {
        if (name is null)
            return new((Contents?)null);
        return cache.GetOrSetAsync(CacheHelpers.MarkdownContentsKey(name), async _ =>
        {
            var contents = await repo.GetContentAsync(userId, name, token);
            return contents.Match(
                (Contents c) => c,
                (Failure _) => (Contents?)null
            );
        }, tags: CacheHelpers.MarkdownContentTags, token: token);
    }

    private static async Task _setCacheEntriesAsync(IFusionCache cache, string name, Contents contents,
        string markdownHtml, CancellationToken token)
    {
        await cache.SetAsync(CacheHelpers.HtmlBodyKey(name), contents with { Body = markdownHtml },
            tags: CacheHelpers.HtmlBodyTags, token: token);
        await cache.SetAsync(CacheHelpers.MarkdownContentsKey(name), contents,
            tags: CacheHelpers.MarkdownContentTags, token: token);
    }

    // unify the handling for both GET and POST:
    // if both formTitle and formContents are null then GET endpoint was matched and we fetch from cache;
    // if neither are null then POST was matched and use contents. The handler lambda is responsible for CSRF validation
    // When nameSlug is null, then we are rendering the edit for the create page.
    public static async Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>> RenderEditPageAsync(
        string? nameSlug, Guid userId, bool isInitiallyPublic, Contents? formData, AppDbContext repo,
        IFusionCache cache, AntiforgeryTokenSet aft, CancellationToken token)
    {
        var contents = formData ?? await _fetchMarkdownAsync(cache, repo, userId, nameSlug, token);
        var isCreatePage = nameSlug is null;
        
        if (contents is null && !isCreatePage)
            return TypedResults.NotFound();
        // edit page for create; compute name slug
        if (contents is not null && isCreatePage)
            nameSlug = contents.Value.ComputeSlugName();
        
        var htmlContents = contents?.RenderHtml() ?? default;
        var toPreviewPage = $"{BLOG_PREFIX}/{nameSlug}{EDIT_SUFFIX}";
        var toSubmitPage = $"{BLOG_PREFIX}/{nameSlug}{SUBMIT_EDIT_SUFFIX}";
        if (isCreatePage)
        {
            toPreviewPage = $"{BLOG_PREFIX}{NEW_SLUG}";
            toSubmitPage = $"{BLOG_PREFIX}{SUBMIT_NEW_SLUG}";
        }

        return Results.Extensions.RazorSlice<BlogEntryEditView, BlogEntryEdit>(
            new BlogEntryEdit(new HtmlString(htmlContents.Body), 
                contents?.Title, contents?.Body,
                toPreviewPage, toSubmitPage, aft,
                isCreatePage ? nameSlug: null, 
                IsNewPost: true, IsInitiallyPublic: isInitiallyPublic));
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
    [LoggerMessage(LogLevel.Debug, "updater: slug {name}: invalidate cache: uid={uid} public={isPublic}")]
    internal static partial void LogUpdater_SlugNameInvalidateCachesByUidAndPublic(ILogger<Routing> logger, 
        string name, Guid uid, bool isPublic);
    
    [LoggerMessage(LogLevel.Information, "updater: commit slug {name}")]
    internal static partial void LogUpdater_CommitBySlugName(ILogger<Routing> logger, string name);

    [LoggerMessage(LogLevel.Information, "submit new: title {title} from {uid} [public={isPublic}]")]
    internal static partial void LogSubmitNew_ForTitleWithUidAndPublic(ILogger<Routing> logger,
        string title, Guid uid, bool isPublic);

    [LoggerMessage(LogLevel.Debug, "insert result: {insertStatus}")]
    internal static partial void LogSubmitNew_InsertResultByStatus(ILogger<Routing> logger, 
        OneOf<string, Failure> insertStatus);
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
        return await next(context);
    }

    [LoggerMessage(LogLevel.Information, 
        "content access permissions: lookup: name={name}, uid={uid}")]
    static partial void LogContentAccessPermissionsNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid);
    [LoggerMessage(LogLevel.Information, 
        "content access permissions: lookup: name={name}, uid={uid}, permissions={perms}")]
    static partial void LogContentAccessPermissionsCompletedNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid, AccessLevel? perms);
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