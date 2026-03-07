using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Slices;
using CsSsg.Src.Slices.ViewModels;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
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
        private void AddBlogHtmlRoutes()
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

    private static Task<Results<BadRequest<string>, RazorSliceHttpResult<ManageEntry>>>
    GetManagePageForNameAsync(string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var initiallyPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        return DoGetManagePageForNameAsync(name, uidFromCookie, initiallyPublic, repo, cache, aft, token);
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

    private static async Task<RazorSliceHttpResult<Listing>> GetAllAvailableBlogEntriesAsync(
        ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache, CancellationToken token,
        [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null)
    {
        var date = beforeOrAt is null
            ? DateTime.UtcNow
            : DateTime.Parse(beforeOrAt, null, DateTimeStyles.RoundtripKind);
        var listing = await DoGetAllAvailableBlogEntriesAsync(auth.TryUid, limit, date, repo, cache, token);
        return Results.Extensions.RazorSlice<BlogListing, Listing>(listing);
    }
}
