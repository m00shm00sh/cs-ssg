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
using CsSsg.Src.Post;
using static CsSsg.Src.Post.RepositoryExtensionsSharedHelpers;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.Slices.Media;
using CsSsg.Src.Slices.ViewModels.Media;
using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Media;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    private static readonly IManageCommand.PostVisibility[] ForbiddenVisibilities = 
        [IManageCommand.PostVisibility.Public];
    internal const string LIST_SUFFIX = "/-list";
    private const string EDIT_SUFFIX = "/edit";
    private const string NEW_SLUG = "/-new";
    private const string MANAGE_SUFFIX = "/manage";
    private const string SUBMIT_RENAME_SUFFIX = "/rename";
    private const string SUBMIT_TAGS_SUFFIX = "/tags";
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
            app.MapGet(MEDIA_PREFIX,
                ExtractUidFromClaimsThenInvokeGetAllAvailableMediaThenTransformResult(
                        listing =>
                        {
                            var listingViewModel = new MediaListing(_makeHeader(),
                                listing.Select(e =>
                                    new MediaListingEntry(e.Slug, LinkForName(e.Slug), e.ContentType, e.Size,
                                        e.AuthorHandle, StringListToTags(e.Tags), e.LastModified,
                                        e.RevisionCount,
                                        ManageLinkForName(e.Slug).TakeIf(_ => e.AccessLevel.IsWrite)
                                    )),
                                ToNewPage: MEDIA_PREFIX);

                            return TypedResults.RazorSlice<MediaListingView, MediaListing>(listingViewModel);
                        }
                    ))
                .UseCookieAuthentication();

            app.MapGet(MEDIA_PREFIX + NAME_SLUG, InvokeDoGetMediaAsync)
                .UseCookieAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter()
                .AddIfModifiedSinceFilter();

            app.MapGet(MEDIA_PREFIX + NAME_SLUG + EDIT_SUFFIX, GetMediaUpdaterForName)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapPost(MEDIA_PREFIX + NAME_SLUG, SubmitMediaUpdateFormForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            app.MapGet(MEDIA_PREFIX + NEW_SLUG, GetMediaCreator)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();
                
            app.MapPost(MEDIA_PREFIX, SubmitMediaCreationFormAsync)
                .UseCookieAuthentication()
                .AddWritePermissionsFilter();

            app.MapGet(MEDIA_PREFIX + NAME_SLUG + MANAGE_SUFFIX, GetManagePageForNameAsync)
                .UseCookieAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_RENAME_SUFFIX, SubmitRenameForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_TAGS_SUFFIX, SubmitChangeTagsForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_AUTHOR_SUFFIX, SubmitChangeAuthorForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            app.MapPost(MEDIA_PREFIX + NAME_SLUG + SUBMIT_DELETE_SUFFIX, SubmitDeleteForNameAsync)
                .UseCookieAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
        }
    }

    private static Results<NotFound, RazorSlice<Upload>> GetMediaUpdaterForName(
        string name, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IAntiforgery af)
    {
        auth.RequireUid();
        var aft = af.GetAndStoreTokens(ctx);
        return RenderUploadPage(name, aft);
    }
    
    // When nameSlug is null, then we are rendering the edit for the create page.
    private static Results<NotFound, RazorSlice<Upload>> RenderUploadPage(string? nameSlug, AntiforgeryTokenSet aft)
    {
        var isCreatePage = nameSlug is null;
        
        var toSubmitPage = LinkForName("");
        if (!isCreatePage)
            toSubmitPage = LinkForName(nameSlug);

        return TypedResults.RazorSlice<UploaderView, Upload>(
            new Upload(_makeHeader(), aft, toSubmitPage, nameSlug));
    }

    private static async Task<IResult> SubmitMediaUpdateFormForNameAsync(
        string name, [FromForm] IFormFile upload, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo,
        IFusionCache cache, IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        var isPublic = ctx.TryGetTags()?.Contains(Post.RepositoryExtensions.TAG_PUBLIC) ?? false;
        var cToken = ctx.RequireConcurrencyToken();
        
        var result = await DoSubmitMediaEditForNameAsync(name, auth, upload.ToObject(), isPublic, 
            cToken, repo, cache, logger, token);
        return result.Match(
            FailureExtensions.AsResult,
            () => Results.Redirect(LinkForName(name)));
    }

    private static RazorSlice<Upload> GetMediaCreator(
        HttpContext ctx, ClaimsPrincipal auth, IAntiforgery af, CancellationToken token)
    {
        auth.RequireUid();
        var aft = af.GetAndStoreTokens(ctx);
        var page = RenderUploadPage(null, aft);
        return (RazorSlice<Upload>)page.Result;
    }
    
    private static async Task<IResult> SubmitMediaCreationFormAsync(
        [FromForm] IFormFile upload, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, ILogger<Routing> logger, CancellationToken token)
    {
        auth.RequireUid();
        var result = await DoSubmitMediaCreationAsync(upload.FileName, upload.ToObject(), auth,
            repo, cache, logger, token);
        return result.Match(insertedName => Results.Redirect(LinkForName(insertedName)),
            FailureExtensions.AsResult);
    }

    private static async Task<Results<BadRequest<string>, RazorSlice<MediaManageEntry>>>
    GetManagePageForNameAsync(string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var cToken = ctx.RequireConcurrencyToken();
        var aft = af.GetAndStoreTokens(ctx);
        var tags = ctx.TryGetTags() ?? [];
        var perms = RepositoryExtensionsSharedHelpers.StringListToTags(tags);
        var stats = await DoGetManagePageForNameAndPermissionAsync(name, perms, cToken, repo, cache, token);
        var hasWritePermission = ctx.TryGetAccessLevel()?.IsWrite ?? false;
        
        var editActions = hasWritePermission ? new ManageEntry.EditMetadataActionLinks(aft,
                InitialVisibility: perms.Visibility,
                RenameActionLink: ActionLinkForName(name, SUBMIT_RENAME_SUFFIX),
                PermissionsActionLink: ActionLinkForName(name, SUBMIT_TAGS_SUFFIX),
                AuthorActionLink: ActionLinkForName(name, SUBMIT_AUTHOR_SUFFIX),
                DeleteActionLink: ActionLinkForName(name, SUBMIT_DELETE_SUFFIX))
            : null;


        if (ForbiddenVisibilities.Contains(perms.Visibility))
            throw new InvalidOperationException($"media:{name}: invalid visibility: {perms.Visibility}");

        return TypedResults.RazorSlice<ManageEntryView, MediaManageEntry>(
            new MediaManageEntry(_makeHeader(),
                SlugName: name, ContentType: stats.ContentType, Size: stats.Size,
                stats.Revisions,
                EditMetadata: editActions));
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
            return (await DoSubmitRenameForNameAsync(name, uid, renameCommand, cToken, repo, cache, logger, token))
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
            var setPermissionsCommand = (IManageCommand.SetTags)mc;
            return (await DoSubmitChangeTagsForNameAsync(name, uid, setPermissionsCommand, cToken, repo, cache,
                    logger, token))
                .Match(FailureExtensions.AsResult,
                    () => Results.Redirect(MEDIA_PREFIX));
        }, ex => Results.BadRequest(ex.Message));
    }
    
    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitChangeAuthorForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var initiallyPublic = ctx.TryGetTags()?.Contains(Post.RepositoryExtensions.TAG_PUBLIC) ?? false;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Author);
        return await formParseResult.MatchAsync(async mc =>
        {
            var authorCommand = (IManageCommand.SetAuthor)mc;
            return (await DoSubmitSetAuthorForNameAsync(name, uid, initiallyPublic, authorCommand, cToken, repo,
                    cache, logger, token))
                .Match(_ => Results.Redirect(MEDIA_PREFIX),
                    FailureExtensions.AsResult);
        }, ex => Results.BadRequest(ex.Message));
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 302 */> SubmitDeleteForNameAsync(
        string name, IFormCollection form, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var initiallyPublic = ctx.TryGetTags()?.Contains(Post.RepositoryExtensions.TAG_PUBLIC) ?? false;
        var formParseResult = IManageCommand.FromForm(form, IManageCommand.FormFrom.Delete);
        return await formParseResult.MatchAsync(async mc =>
        {
            var _ = (IManageCommand.Delete)mc; // type check and discard
            return (await DoDeleteMediumAsync(name, initiallyPublic, uid, cToken, repo, cache, logger, token))
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
