using System.Diagnostics.CodeAnalysis;
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
using CsSsg.Src.SharedTypes;
using CsSsg.Src.Slices.Post;
using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Post;
using static RepositoryExtensionsSharedHelpers;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    // also used by User.RoutingExtensions
    internal const string BLOG_PREFIX = "/blog";
    // also used by Post.RoutingExtensions.JsonApi
    private const string RX_SLUG_WITH_OPT_UUID = @"^\w+(-\w+)*(\.[0-9a-f]{{32}})?$";
    [StringSyntax("Route")] private const string NAME_SLUG = $"/{{name:regex({RX_SLUG_WITH_OPT_UUID})}}";
    
    private const string EDIT_SUFFIX = "/edit";
    internal const string NEW_SLUG = "/-new";
    private const string MANAGE_SUFFIX = "/manage";
    private const string SUBMIT_RENAME_SUFFIX = "/rename";
    private const string SUBMIT_TAGS_SUFFIX = "/perms";
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
            app.MapGet(BLOG_PREFIX, 
                TryExtractUidFromOptionalClaimsThenInvokeGetAllAvailableBlogEntriesThenTransformResultAsync(
                        (listing, uid) =>
                        {
                            var listingViewModel = new Listing(_makeHeader(uid.HasValue), 
                                listing.Select(e =>
                                    new ListingEntry(e.LatestTitle, LinkForName(e.Slug),
                                        e.AuthorHandle, StringListToTags(e.Tags), e.LastModified,
                                        e.RevisionCount,
                                        ManageLinkForName(e.Slug).TakeIf(_ => e.AccessLevel == AccessLevel.FullControl)
                                    ))
                            );
        
                            return TypedResults.RazorSlice<BlogListing, Listing>(listingViewModel);
                        })
                )
                .UseCookieAuthentication()
                .AllowAnonymous();

            app.MapGet(BLOG_PREFIX + NAME_SLUG, GetBlogEntryHtmlForNameAsync)
                .UseCookieAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter()
                .AddIfModifiedSinceFilter();

            app.MapGet(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, GetBlogEntryEditorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX, PostBlogEntryEditorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapPost(BLOG_PREFIX + NAME_SLUG, SubmitBlogEntryEditFormForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapGet(BLOG_PREFIX + NEW_SLUG, GetBlogEntryCreatorAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();
                
            app.MapPost(BLOG_PREFIX + NEW_SLUG, PostBlogEntryCreatorAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();

            app.MapPost(BLOG_PREFIX, SubmitBlogEntryCreationFormAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();
            
            app.MapGet(BLOG_PREFIX + NAME_SLUG + MANAGE_SUFFIX, GetManagePageForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_RENAME_SUFFIX, SubmitRenameForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_TAGS_SUFFIX, SubmitChangeTagsForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_AUTHOR_SUFFIX, SubmitChangeAuthorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_DELETE_SUFFIX, SubmitDeleteForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();

            app.MapGet("/", () => Results.Redirect(BLOG_PREFIX));
            app.MapGet("/contact", () => Results.Redirect(LinkForName("contact")));
        }
    }

    private static async Task<Results<RazorSlice<BlogEntry>, NotFound>>
    GetBlogEntryHtmlForNameAsync(string name, HttpContext ctx, ClaimsPrincipal? auth, AppDbContext repo,
        IFusionCache cache, CancellationToken token, int revision = 0)
    {
        var uid = auth?.TryGetUid();
        var cToken = ctx.RequireConcurrencyToken();
        var contents = await DoGetRenderedBlogEntryForNameAsync(name, cToken, repo, cache, token, revision);
        var hasWritePermission = ctx.TryGetAccessLevel()?.IsWrite ?? false;

        var editPage = hasWritePermission ? ActionLinkForName(name) : null;
        // unwrap from monad to nullable so that we get the desired type inference
        if (contents.IsNone)
            return TypedResults.NotFound();
        var (title, article, mtime) = contents.ToNullable()!.Value;
        ctx.SetModifiedSinceValue(mtime);
        return TypedResults.RazorSlice<BlogEntryView, BlogEntry>(
            new BlogEntry(_makeHeader(uid.HasValue),
                Title: title,
                Contents: new HtmlString(article),
                ToEditPage: editPage));
    }

    private static Task<Results<NotFound, RazorSlice<BlogEntryEdit>>>
    GetBlogEntryEditorForNameAsync(string name, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo,
        IFusionCache cache, IAntiforgery af, CancellationToken token)
    {
        auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var aft = af.GetAndStoreTokens(ctx);
        return RenderEditPageAsync(name, cToken, null, repo, cache, aft, token);
    }
    
    private static Task<Results<NotFound, RazorSlice<BlogEntryEdit>>>
    PostBlogEntryEditorForNameAsync(string name, [FromForm] EditorFormContents contents, HttpContext ctx,
    ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af,
    CancellationToken token)
    {
        auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var aft = af.GetTokens(ctx);
        return RenderEditPageAsync(name, cToken, contents, repo, cache, aft, token);
    }
    
    // unify the handling for both GET and POST:
    // if both formTitle and formContents are null then GET endpoint was matched and we fetch from cache;
    // if neither are null then POST was matched and use contents. The handler lambda is responsible for CSRF validation
    // When nameSlug is null, then we are rendering the edit for the create page.
    private static async Task<Results<NotFound, RazorSlice<BlogEntryEdit>>> RenderEditPageAsync(
        string? nameSlug, RepositoryExtensions.ConcurrencyToken cToken, Contents? formData, AppDbContext repo,
        IFusionCache cache, AntiforgeryTokenSet aft, CancellationToken token)
    {
        var contents = formData ?? await FetchMarkdownAsync(nameSlug, cToken, repo, cache, token);
        var isCreatePage = nameSlug is null;
        
        if (contents.IsNone && !isCreatePage)
            return TypedResults.NotFound();
        // edit page for create; compute name slug
        if (contents.IsSome && isCreatePage)
            nameSlug = contents.Map(c => c.ComputeSlugName()).ValueUnsafe();
        
        var htmlContents = contents.Map(c => c.RenderHtml()).ToNullable() ?? default;
        var toPreviewPage = LinkForName(NEW_SLUG[1..]);
        var toSubmitPage = LinkForName("");
        if (!isCreatePage)
        {
            toPreviewPage = ActionLinkForName(nameSlug);
            toSubmitPage = LinkForName(nameSlug);
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
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var isPublic = ctx.TryGetTags()?.Contains(RepositoryExtensions.TAG_PUBLIC) ?? false;
        var result = await DoSubmitBlogEntryEditForNameAsync(name, uid, contents, isPublic, cToken,
            repo, cache, logger, token);
        return result.Match(
            FailureExtensions.AsResult,
            () => Results.Redirect(LinkForName(name)));
    }

    private static async Task<RazorSlice<BlogEntryEdit>>
    GetBlogEntryCreatorAsync(HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        auth.RequireUid();
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var aft = af.GetAndStoreTokens(ctx);
        var page = await RenderEditPageAsync(null, cToken, null, repo, cache, aft, token);
        return (RazorSlice<BlogEntryEdit>)page.Result;
    }
    
    private static async Task<RazorSlice<BlogEntryEdit>>
    PostBlogEntryCreatorAsync([FromForm] EditorFormContents contents, HttpContext ctx, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache, IAntiforgery af, CancellationToken token)
    {
        auth.RequireUid();
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var aft = af.GetTokens(ctx);
        var page = await RenderEditPageAsync(null, cToken, contents, repo, cache, aft, token);
        return (RazorSlice<BlogEntryEdit>)page.Result;
    }

    private static async Task<IResult> SubmitBlogEntryCreationFormAsync(
        [FromForm] EditorFormContents content, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var result = await DoSubmitBlogEntryCreationAsync(content, uid, repo, cache, logger, token);
        return result.Match(insertResult => Results.Redirect(LinkForName(insertResult)),
            FailureExtensions.AsResult);
    }

    private static async Task<Results<BadRequest<string>, RazorSlice<ManageEntry>>>
    GetManagePageForNameAsync(string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var aft = af.GetAndStoreTokens(ctx);
        var tags = ctx.TryGetTags() ?? [];
        var perms = StringListToTags(tags);
        var stats = await DoGetManagePageForNameAndPermissionAsync(name, uid, perms, cToken, repo, cache, token);
        var lastRevision = (Revision)stats.Revisions.First(r => r is Revision);
        
        return TypedResults.RazorSlice<ManageEntryView, ManageEntry>(
            new ManageEntry(_makeHeader(true), aft,
                SlugName: name, Title: lastRevision.Title, Size: lastRevision.ContentLength,
                InitialVisibility: perms.Visibility,
                RenameActionLink: ActionLinkForName(name, SUBMIT_RENAME_SUFFIX),
                PermissionsActionLink: ActionLinkForName(name, SUBMIT_TAGS_SUFFIX),
                AuthorActionLink: ActionLinkForName(name, SUBMIT_AUTHOR_SUFFIX),
                DeleteActionLink: ActionLinkForName(name, SUBMIT_DELETE_SUFFIX)));
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitRenameForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Rename);
        return await formParseResult.MatchAsync(async mc =>
        {
            var renameCommand = (IManageCommand.Rename)mc;
            return (await DoSubmitRenameForNameAsync(name, uid, renameCommand, cToken,
                    repo, cache, logger, token))
                .Match(s => Results.Redirect(LinkForName(s)),
                    FailureExtensions.AsResult);
        }, ex => Results.BadRequest(ex.Message));
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitChangeTagsForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Tags);
        return await formParseResult.MatchAsync(async mc =>
        {
            var setTagsCommand = (IManageCommand.SetTags)mc;
            return (await DoSubmitChangeTagsForNameAsync(name, uid, setTagsCommand, cToken, repo, cache,
                    logger, token))
                .Match(FailureExtensions.AsResult,
                    () => Results.Redirect(BLOG_PREFIX));
        }, ex => Results.BadRequest(ex.Message));
    }
    
    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitChangeAuthorForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var initiallyPublic = ctx.TryGetTags()?.Contains(RepositoryExtensions.TAG_PUBLIC) ?? false;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Author);
        return await formParseResult.MatchAsync(async mc =>
        {
            var authorCommand = (IManageCommand.SetAuthor)mc;
            return (await DoSubmitSetAuthorForNameAsync(name, uid, initiallyPublic, authorCommand, cToken, repo,
                    cache, logger, token))
                .Match(_ => Results.Redirect(BLOG_PREFIX),
                    FailureExtensions.AsResult);
        }, ex => Results.BadRequest(ex.Message));
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitDeleteForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var initiallyPublic = ctx.TryGetTags()?.Contains(RepositoryExtensions.TAG_PUBLIC) ?? false;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Delete);
        return await formParseResult.MatchAsync(async mc =>
        {
            var _ = (IManageCommand.Delete)mc; // type check and discard
            return (await DoDeleteBlogEntryAsync(name, initiallyPublic, uid, cToken, repo, cache, logger, token))
                .Match(FailureExtensions.AsResult,
                    () => Results.Redirect(BLOG_PREFIX));
        }, ex => Results.BadRequest(ex.Message));
    }

    private static PostLayout _makeHeader(bool isLoggedIn)
        => new PostLayout(
            NewPostLink: isLoggedIn ? LinkForName(NEW_SLUG[1..]) : null,
            MediaHomeLink: isLoggedIn ? Media.RoutingExtensions.MEDIA_PREFIX + Media.RoutingExtensions.LIST_SUFFIX: null,
            UserLink: isLoggedIn ? User.RoutingExtensions.USER_PREFIX : User.RoutingExtensions.LOGIN_ENDPOINT
        );
}
