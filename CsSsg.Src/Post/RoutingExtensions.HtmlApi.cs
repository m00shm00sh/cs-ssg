using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using KotlinScopeFunctions;
using LanguageExt.UnsafeValueAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RazorSlices;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using static CsSsg.Src.Post.FilterConfigurationExtensions;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.Slices.Post;
using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    // also used by User.RoutingExtensions
    internal const string BLOG_PREFIX = "/blog";
    private const string RX_SLUG_WITH_OPT_UUID = @"^\w+(-\w+)*(\.[[0-9a-f]]{{32}})?$";
    [StringSyntax("Route")] private const string NAME_SLUG = $"/{{name:regex({RX_SLUG_WITH_OPT_UUID})}}";
    
    private const string EDIT_SUFFIX = "/edit";
    private const string SUBMIT_EDIT_SUFFIX = "/edit.1";
    internal const string NEW_SLUG = "/-new";
    private const string SUBMIT_NEW_SLUG = "/-new.1";
    private const string MANAGE_SUFFIX = "/manage";
    private const string SUBMIT_RENAME_SUFFIX = "/rename";
    private const string SUBMIT_PERMISSIONS_SUFFIX = "/perms";
    private const string SUBMIT_AUTHOR_SUFFIX = "/author";
    private const string SUBMIT_DELETE_SUFFIX = "/delete";
    
    internal static string LinkForName(string? name)
        => $"{BLOG_PREFIX}/{name}";
    private static string ActionLinkForName(string? name, string action = EDIT_SUFFIX)
        => LinkForName(name) + action;
    private static string ManageLinkForName(string name)
        => LinkForName(name) + MANAGE_SUFFIX;
    
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
                .AddContentAccessPermissionsFilter();

            app.MapGet(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, GetBlogEntryEditorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, PostBlogEntryEditorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_EDIT_SUFFIX, SubmitBlogEntryEditFormForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapGet(BLOG_PREFIX + NEW_SLUG, GetBlogEntryCreatorAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();
                
            app.MapPost(BLOG_PREFIX + NEW_SLUG, PostBlogEntryCreatorAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + SUBMIT_NEW_SLUG, SubmitBlogEntryCreationFormAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();
            
            app.MapGet(BLOG_PREFIX + NAME_SLUG + MANAGE_SUFFIX, GetManagePageForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_RENAME_SUFFIX, SubmitRenameForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_PERMISSIONS_SUFFIX, SubmitChangePermissionsForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_AUTHOR_SUFFIX, SubmitChangeAuthorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_DELETE_SUFFIX, SubmitDeleteForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapGet("/", () => Results.Redirect(BLOG_PREFIX));
            app.MapGet("/contact", () => Results.Redirect(LinkForName("contact")));
        }
    }

    private static async Task<Results<RazorSlice<BlogEntry>, NotFound>>
    GetBlogEntryHtmlForNameAsync(string name, HttpContext ctx, ClaimsPrincipal? auth, AppDbContext repo,
        IFusionCache cache, CancellationToken token)
    {
        var uidFromAuth = auth?.TryCookieUid;
        var contents = await DoGetRenderedBlogEntryForNameAsync(name, uidFromAuth, repo, cache, token);
        var hasWritePermission = ctx.TryGetAccessLevel()?.IsWrite is not null;

        var editPage = hasWritePermission ? ActionLinkForName(name) : null;
        // unwrap from monad to nullable so that we get the desired type inference
        return contents.ToNullable() is var (title, article)
            ? TypedResults.RazorSlice<BlogEntryView, BlogEntry>(
                new BlogEntry(_makeHeader(uidFromAuth.HasValue),
                    Title: title,
                    Contents: new HtmlString(article),
                    ToEditPage: editPage))
            : TypedResults.NotFound();
    }

    private static Task<Results<NotFound, RazorSlice<BlogEntryEdit>>>
    GetBlogEntryEditorForNameAsync(string name, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo,
        IFusionCache cache, IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        return RenderEditPageAsync(name, uidFromCookie, null, repo, cache, aft, token);
    }
    
    private static Task<Results<NotFound, RazorSlice<BlogEntryEdit>>>
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
    private static async Task<Results<NotFound, RazorSlice<BlogEntryEdit>>> RenderEditPageAsync(
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
            toPreviewPage = ActionLinkForName(nameSlug);
            toSubmitPage = ActionLinkForName(nameSlug, SUBMIT_EDIT_SUFFIX);
        }

        return TypedResults.RazorSlice<BlogEntryEditView, BlogEntryEdit>(
            new BlogEntryEdit(_makeHeader(true), aft,
                PreviewHtml: new HtmlString(htmlContents.Body),
                EditContents: contents.ToNullable(), 
                ToPreviewPage: toPreviewPage, ToSubmitPage: toSubmitPage, 
                CandidateSlugNameForNewPost: isCreatePage ? nameSlug: null, 
                IsNewPost: isCreatePage));
    }

    private static async Task<IResult> SubmitBlogEntryEditFormForNameAsync(
        string name, [FromForm] EditorFormContents contents, HttpContext ctx, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var isPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var result = await DoSubmitBlogEntryEditForNameAsync(name, uidFromCookie, contents, isPublic, repo, cache,
            logger, token);
        return result.Match(
            FailureExtensions.AsResult,
            () => Results.Redirect(LinkForName(name)));
    }

    private static async Task<RazorSlice<BlogEntryEdit>>
    GetBlogEntryCreatorAsync(HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var page = await RenderEditPageAsync(null, uidFromCookie, null, repo, cache, aft, token);
        return (RazorSlice<BlogEntryEdit>)page.Result;
    }
    
    private static async Task<RazorSlice<BlogEntryEdit>>
    PostBlogEntryCreatorAsync([FromForm] EditorFormContents contents, HttpContext ctx, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache, IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetTokens(ctx);
        var page = await RenderEditPageAsync(null, uidFromCookie, contents, repo, cache, aft, token);
        return (RazorSlice<BlogEntryEdit>)page.Result;
    }

    private static async Task<IResult> SubmitBlogEntryCreationFormAsync(
        [FromForm] EditorFormContents content, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var result = await DoSubmitBlogEntryCreationAsync(content, uidFromCookie, repo, cache, logger, token);
        return await result.MatchAsync(async insertedName =>
            {
                // if the insert didn't have a dot in it, it's not from an on-conflict-rename, meaning that it
                // could've come from after a failed update which set the access cache; clear the access entry to be
                // safe of that case
                if (!insertedName.Contains('.'))
                    await ContentAccessPermissionFilter.InvalidateAccessCacheForKeyAsync(logger, cache, 
                        ContentAccessFilterConfig, "insert", uidFromCookie, insertedName, token);
                return Results.Redirect(LinkForName(insertedName));
            },
            FailureExtensions.AsResult);
    }

    private static async Task<Results<BadRequest<string>, RazorSlice<ManageEntry>>>
    GetManagePageForNameAsync(string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var initiallyPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var perms = new IManageCommand.Permissions
        {
            Public = initiallyPublic
        };
        var stats = await DoGetManagePageForNameAndPermissionAsync(name, uidFromCookie, perms, repo, cache, token);
        
        return TypedResults.RazorSlice<ManageEntryView, ManageEntry>(
            new ManageEntry(_makeHeader(true), aft,
                SlugName: name, Title: stats.Title, Size: stats.ContentLength, InitiallyPublic: initiallyPublic,
                RenameActionLink: ActionLinkForName(name, SUBMIT_RENAME_SUFFIX),
                PermissionsActionLink: ActionLinkForName(name, SUBMIT_PERMISSIONS_SUFFIX),
                AuthorActionLink: ActionLinkForName(name, SUBMIT_AUTHOR_SUFFIX),
                DeleteActionLink: ActionLinkForName(name, SUBMIT_DELETE_SUFFIX)));
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitRenameForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Rename);
        return await formParseResult.MatchAsync(async mc =>
        {
            var renameCommand = (IManageCommand.Rename)mc;
            return (await DoSubmitRenameForNameAsync(name, uidFromCookie, renameCommand,
                    repo, cache, logger, token))
                .Match(s => Results.Redirect(LinkForName(s)),
                    FailureExtensions.AsResult);
        }, ex => Results.BadRequest(ex.Message));
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitChangePermissionsForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Permissions);
        return await formParseResult.MatchAsync(async mc =>
        {
            var setPermissionsCommand = (IManageCommand.SetPermissions)mc;
            return (await DoSubmitChangePermissionsForNameAsync(name, uidFromCookie, setPermissionsCommand, repo, cache,
                    logger, token))
                .Match(FailureExtensions.AsResult,
                    () => Results.Redirect(BLOG_PREFIX));
        }, ex => Results.BadRequest(ex.Message));
    }
    
    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitChangeAuthorForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var initiallyPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Author);
        return await formParseResult.MatchAsync(async mc =>
        {
            var authorCommand = (IManageCommand.SetAuthor)mc;
            return (await DoSubmitSetAuthorForNameAsync(name, uidFromCookie, initiallyPublic, authorCommand, repo,
                    cache, logger, token))
                .Match(_ => Results.Redirect(BLOG_PREFIX),
                    FailureExtensions.AsResult);
        }, ex => Results.BadRequest(ex.Message));
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitDeleteForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var initiallyPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Delete);
        return await formParseResult.MatchAsync(async mc =>
        {
            var _ = (IManageCommand.Delete)mc; // type check and discard
            return (await DoDeleteBlogEntryAsync(name, initiallyPublic, uidFromCookie, repo, cache, logger, token))
                .Match(FailureExtensions.AsResult,
                    () => Results.Redirect(BLOG_PREFIX));
        }, ex => Results.BadRequest(ex.Message));
    }

    private static async Task<RazorSlice<Listing>> GetAllAvailableBlogEntriesPageAsync(
        ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache, CancellationToken token,
        [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null)
    {
        var uidFromAuth = auth?.TryCookieUid;
        var date = beforeOrAt is null
            ? DateTime.UtcNow
            : DateTime.Parse(beforeOrAt, null, DateTimeStyles.RoundtripKind);
        
        var listing = await DoGetAllAvailableBlogEntriesAsync(uidFromAuth, limit, date, repo, cache, token);
        
        var listingViewModel = new Listing(_makeHeader(uidFromAuth.HasValue), 
            listing.Select(e =>
                new ListingEntry(e.Title, LinkForName(e.Slug),
                    e.AuthorHandle, e.IsPublic, e.LastModified,
                    ManageLinkForName(e.Slug).TakeIf(_ => e.AccessLevel.IsWrite)
                ))
        );
        
        return TypedResults.RazorSlice<BlogListing, Listing>(listingViewModel);
    }

    private static PostLayout _makeHeader(bool isLoggedIn)
        => new PostLayout(
            NewPostLink: isLoggedIn ? LinkForName(NEW_SLUG[1..]) : null,
            MediaHomeLink: isLoggedIn ? Media.RoutingExtensions.MEDIA_PREFIX + Media.RoutingExtensions.LIST_SUFFIX: null,
            UserLink: isLoggedIn ? User.RoutingExtensions.USER_PREFIX : User.RoutingExtensions.LOGIN_ENDPOINT
        );

}
