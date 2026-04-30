using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using KotlinScopeFunctions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RazorSlices;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using static CsSsg.Src.Media.FilterConfigurationExtensions;
using CsSsg.Src.Post;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.Slices.Media;
using CsSsg.Src.Slices.ViewModels.Media;
using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Media;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    internal const string LIST_SUFFIX = "/-list";
    private const string EDIT_SUFFIX = "/edit";
    private const string SUBMIT_EDIT_SUFFIX = "/edit.1";
    private const string NEW_SLUG = "/-new";
    private const string SUBMIT_NEW_SLUG = "/-new.1";
    private const string MANAGE_SUFFIX = "/manage";
    private const string SUBMIT_RENAME_SUFFIX = "/rename";
    private const string SUBMIT_PERMISSIONS_SUFFIX = "/perms";
    private const string SUBMIT_AUTHOR_SUFFIX = "/author";
    private const string SUBMIT_DELETE_SUFFIX = "/delete";
    
    private static string LinkForName(string? name)
        => $"{MEDIA_PREFIX}/{name}";
    private static string ActionLinkForName(string? name, string action = EDIT_SUFFIX)
        => LinkForName(name) + action;
    private static string ManageLinkForName(string name)
        => LinkForName(name) + MANAGE_SUFFIX;
    
    extension(WebApplication app)
    {
        private void AddMediaHtmlRoutes()
        {
            app.MapGet(MEDIA_PREFIX + LIST_SUFFIX,
                ExtractUidFromClaimsThenInvokeGetAllAvailableMediaThenTransformResult(
                        listing =>
                        {
                            var listingViewModel = new MediaListing(_makeHeader(),
                                listing.Select(e =>
                                    new MediaListingEntry(e.Slug, LinkForName(e.Slug), e.ContentType, e.Size,
                                        e.AuthorHandle, e.IsPublic, e.LastModified,
                                        ManageLinkForName(e.Slug).TakeIf(_ => e.AccessLevel.IsWrite)
                                    )),
                                ToNewPage: MEDIA_PREFIX + NEW_SLUG);

                            return TypedResults.RazorSlice<MediaListingView, MediaListing>(listingViewModel);
                        }
                    ))
                .UseCookieAuthentication();
            
            app.MapGet(MEDIA_PREFIX + NAME_SLUG,
                    TryExtractUidFromOptionalClaimsThenInvokeDoGetMediaAsync(auth => auth?.TryCookieUid)
                )
                .UseCookieAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter();

            app.MapGet(MEDIA_PREFIX + NAME_SLUG + EDIT_SUFFIX, GetMediaUpdaterForName)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_EDIT_SUFFIX, SubmitMediaUpdateFormForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapGet(MEDIA_PREFIX + NEW_SLUG, GetMediaCreator)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();
                
            app.MapPost(MEDIA_PREFIX + SUBMIT_NEW_SLUG, SubmitMediaCreationFormAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();

            app.MapGet(MEDIA_PREFIX + NAME_SLUG + MANAGE_SUFFIX, GetManagePageForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_RENAME_SUFFIX, SubmitRenameForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_PERMISSIONS_SUFFIX, SubmitChangePermissionsForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_AUTHOR_SUFFIX, SubmitChangeAuthorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_DELETE_SUFFIX, SubmitDeleteForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
        }
    }

    private static Results<NotFound, RazorSlice<Upload>> GetMediaUpdaterForName(
        string name, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IAntiforgery af)
    {
        var _ = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        return RenderUploadPage(name, aft);
    }
    
    // When nameSlug is null, then we are rendering the edit for the create page.
    private static Results<NotFound, RazorSlice<Upload>> RenderUploadPage(string? nameSlug, AntiforgeryTokenSet aft)
    {
        var isCreatePage = nameSlug is null;
        
        var toSubmitPage = LinkForName(SUBMIT_NEW_SLUG[1..]);
        if (!isCreatePage)
            toSubmitPage = ActionLinkForName(nameSlug, SUBMIT_EDIT_SUFFIX);

        return TypedResults.RazorSlice<UploaderView, Upload>(
            new Upload(_makeHeader(), aft, toSubmitPage, nameSlug));
    }

    private static async Task<IResult> SubmitMediaUpdateFormForNameAsync(
        string name, [FromForm] IFormFile upload, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo,
        IFusionCache cache, IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var isPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        
        var result = await DoSubmitMediaEditForNameAsync(name, uidFromCookie, upload.ToObject(), isPublic,
            repo, cache, logger, token);
        return result.Match(
            FailureExtensions.AsResult,
            () => Results.Redirect(LinkForName(name)));
    }

    private static RazorSlice<Upload> GetMediaCreator(
        HttpContext ctx, ClaimsPrincipal auth, IAntiforgery af, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var page = RenderUploadPage(null, aft);
        return (RazorSlice<Upload>)page.Result;
    }
    
    private static async Task<IResult> SubmitMediaCreationFormAsync(
        [FromForm] IFormFile upload, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromCookie = auth.RequireUid;
        var result = await DoSubmitMediaCreationAsync(upload.FileName, upload.ToObject(), uidFromCookie,
            repo, cache, logger, token);
        return await result.MatchAsync(async insertedName =>
            {
                // safe of that case
                if (!insertedName.Contains('.'))
                    await ContentAccessPermissionFilter.InvalidateAccessCacheForKeyAsync(logger, cache, 
                        ContentAccessFilterConfig, "insert", uidFromCookie, insertedName, token);
                return Results.Redirect(LinkForName(insertedName));
            },
            FailureExtensions.AsResult);
    }

    private static async Task<Results<BadRequest<string>, RazorSlice<MediaManageEntry>>>
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
        
        return TypedResults.RazorSlice<ManageEntryView, MediaManageEntry>(
            new MediaManageEntry(_makeHeader(), aft,
                SlugName: name, ContentType: stats.ContentType, Size: stats.Size, InitiallyPublic: initiallyPublic,
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
                    () => Results.Redirect(MEDIA_PREFIX));
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
                .Match(_ => Results.Redirect(MEDIA_PREFIX),
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
            return (await DoDeleteMediumAsync(name, initiallyPublic, uidFromCookie, repo, cache, logger, token))
                .Match(FailureExtensions.AsResult,
                    () => Results.Redirect(MEDIA_PREFIX));
        }, ex => Results.BadRequest(ex.Message));
    }

    private static PostLayout _makeHeader()
        => new PostLayout(
            NewPostLink: Post.RoutingExtensions.LinkForName(Post.RoutingExtensions.NEW_SLUG),
            MediaHomeLink: MEDIA_PREFIX + LIST_SUFFIX,
            UserLink: User.RoutingExtensions.USER_PREFIX
        );

    extension(IFormFile file)
    {
        private Object ToObject()
        => new Object(file.ContentType, file.OpenReadStream());
    }
}
