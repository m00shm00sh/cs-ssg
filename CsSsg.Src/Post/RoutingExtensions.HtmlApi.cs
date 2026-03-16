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
using KotlinScopeFunctions;
using LanguageExt.UnsafeValueAccess;

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
            app.MapGet(BLOG_PREFIX, GetAllAvailableBlogEntriesPageAsync)
                .UseCookieAuthentication()
                .AllowAnonymous();
            
            app.MapGet(BLOG_PREFIX + NAME_SLUG, GetBlogEntryHtmlForNameAsync)
                .UseCookieAuthentication()
                .AllowAnonymous()
                .AddEndpointFilter<ContentAccessPermissionFilter>();

            app.MapGet(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, GetBlogEntryEditorForNameAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, PostBlogEntryEditorForNameAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_EDIT_SUFFIX, SubmitBlogEntryEditFormForNameAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapGet(BLOG_PREFIX + NEW_SLUG, GetBlogEntryCreatorAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<WritePermissionFilter>();
                
            app.MapPost(BLOG_PREFIX + NEW_SLUG, PostBlogEntryCreatorAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<WritePermissionFilter>();
            
            app.MapPost(BLOG_PREFIX + SUBMIT_NEW_SLUG, SubmitBlogEntryCreationFormAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapGet(BLOG_PREFIX + NAME_SLUG + MANAGE_SUFFIX, GetManagePageForNameAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_MANAGE_SUFFIX, SubmitManageEntryPageForNameAsync)
                .UseCookieAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            app.MapGet("/", () => Results.Redirect(BLOG_PREFIX));
            app.MapGet("/contact", () => Results.Redirect(LinkForName("contact")));
        }
    }

    private static async Task<Results<RazorSliceHttpResult<BlogEntry>, NotFound>>
    GetBlogEntryHtmlForNameAsync(string name, HttpContext ctx, ClaimsPrincipal? auth, AppDbContext repo,
        IFusionCache cache, CancellationToken token)
    {
        var uidFromAuth = auth?.TryCookieUid;
        var contents = await DoGetRenderedBlogEntryForNameAsync(name, uidFromAuth, repo, cache, token);
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

    private static async Task<IResult> SubmitBlogEntryEditFormForNameAsync(
        string name, [FromForm] EditorFormContents contents, HttpContext ctx, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var isPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var result = await DoSubmitBlogEntryEditForNameAsync(name, uidFromCookie, contents, isPublic, repo, cache,
            logger, token);
        return result.Match(
            failCode => failCode.AsResult,
            () => Results.Redirect(LinkForName(name)));
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

    private static async Task<IResult> SubmitBlogEntryCreationFormAsync(
        [FromForm] EditorFormContents content, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var result = await DoSubmitBlogEntryCreationAsync(content, uidFromCookie, repo, cache, logger, token);
        return await result.MatchAsync(
            failCode => failCode.AsResult,
            async insertedName =>
            {
                // if the insert didn't have a dot in it, it's not from an on-conflict-rename, meaning that it
                // could've come from after a failed update which set the access cache; clear the access entry to be
                // safe of that case
                if (!insertedName.Contains('.'))
                    await ContentAccessPermissionFilter.InvalidateAccessCacheForKeyAsync(logger, cache, "insert",
                        uidFromCookie, insertedName, token);
                return Results.Redirect(LinkForName(insertedName));
            });
    }

    private static async Task<Results<BadRequest<string>, RazorSliceHttpResult<ManageEntry>>>
    GetManagePageForNameAsync(string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var initiallyPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var perms = new ManageCommand.Permissions
        {
            Public = initiallyPublic
        };
        var stats = await DoGetManagePageForNameAsync(name, uidFromCookie, perms, repo, cache, token);
        
        return Results.Extensions.RazorSlice<ManageEntryView, ManageEntry>(
            new ManageEntry(name, stats.Title, stats.ContentLength, ManageLinkForName(name, SUBMIT_MANAGE_SUFFIX),
                initiallyPublic, aft));
    }

    private static Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitManageEntryPageForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx,
        AppDbContext repo, IFusionCache cache, IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var initiallyPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var formParseResult = ManageCommand.FromForm(form);
        return formParseResult.MatchAsync(
            argEx => Task.FromResult(Results.BadRequest(argEx.Message)),
            command => DoSubmitManageEntryPageForNameAsync(name, uidFromCookie, initiallyPublic, command, repo,
                cache, logger, token)
        );
    }

    private static async Task<RazorSliceHttpResult<Listing>> GetAllAvailableBlogEntriesPageAsync(
        ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache, CancellationToken token,
        [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null)
    {
        var uidFromAuth = auth?.TryCookieUid;
        var date = beforeOrAt is null
            ? DateTime.UtcNow
            : DateTime.Parse(beforeOrAt, null, DateTimeStyles.RoundtripKind);
        
        var listing = await DoGetAllAvailableBlogEntriesAsync(uidFromAuth, limit, date, repo, cache, token);
        
        var listingViewModel = new Listing(listing.Select(e =>
                new ListingEntry(e.Title, LinkForName(e.Slug),
                    e.AuthorHandle, e.IsPublic, e.LastModified,
                    ManageLinkForName(e.Slug).TakeIf(_ => e.AccessLevel.IsWrite)
                )),
            CanModify: uidFromAuth is not null,
            ToNewPostPage: uidFromAuth?.Let(_ => LinkForName(NEW_SLUG[1..]))
        );
        
        return Results.Extensions.RazorSlice<BlogListing, Listing>(listingViewModel);
    }
}
