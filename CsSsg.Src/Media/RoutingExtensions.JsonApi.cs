using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using KotlinScopeFunctions;
using Microsoft.Net.Http.Headers;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using CsSsg.Src.Post;
using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.Media;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    private const string STATS_SUFFIX = "/stats";
    private const string RENAME_SUFFIX = "/rename";
    private const string TAGS_SUFFIX = "/tags";
    private const string CHANGE_AUTHOR_SUFFIX = "/chauthor";
    
    extension(WebApplication app)
    {
        private void AddMediaJsonRoutes(string apiPrefix)
        {
            var apiGroup = app.MapGroup(apiPrefix);

            apiGroup.MapGet(MEDIA_PREFIX, ExtractUidFromClaimsThenInvokeGetAllAvailableMediaThenTransformResult(
                    entries => entries.ToList()))
                .UseJwtBearerAuthentication();

            apiGroup.MapGet(MEDIA_PREFIX + NAME_SLUG, InvokeDoGetMediaAsync)
                .UseJwtBearerAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter()
                .AddIfModifiedSinceFilter();

            apiGroup.MapPut(MEDIA_PREFIX + NAME_SLUG, SubmitMediaEditForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            apiGroup.MapPost(MEDIA_PREFIX, SubmitMediaCreationAsync)
                .UseJwtBearerAuthentication()
                .AddWritePermissionsFilter();

            apiGroup.MapGet(MEDIA_PREFIX + NAME_SLUG + STATS_SUFFIX, GetStatsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            apiGroup.MapPost(MEDIA_PREFIX + NAME_SLUG + RENAME_SUFFIX, RenameMediaEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            apiGroup.MapPost(MEDIA_PREFIX + NAME_SLUG + TAGS_SUFFIX, ChangeTagsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            apiGroup.MapPost(MEDIA_PREFIX + NAME_SLUG + CHANGE_AUTHOR_SUFFIX, ChangeAuthorForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            apiGroup.MapDelete(MEDIA_PREFIX + NAME_SLUG, DeleteMediaEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
        }
    }

    private static async Task<IResult> SubmitMediaEditForNameAsync(string name, HttpContext ctx, HttpRequest req,
        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
    {
        var cToken = ctx.RequireConcurrencyToken();
        var isPublic = ctx.TryGetTags()?.Contains(Post.RepositoryExtensions.TAG_PUBLIC) ?? false;
        var cType = req.ContentType;
        if (cType is null)
            return Results.BadRequest("missing content-type header");
        var contents = new Object(cType, req.Body);
        var result = await DoSubmitMediaEditForNameAsync(name, auth, contents, isPublic, cToken, repo, cache,
            logger, token);
        return result.Match(FailureExtensions.AsResult,
            Results.NoContent);
    }

    private static async Task<IResult> SubmitMediaCreationAsync(HttpContext ctx, HttpRequest req, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        auth.RequireUid();
        var filename = req.GetFilenameFromContentDisposition();
        if (filename is null)
            return  Results.BadRequest("missing content-disposition header with filename parameter");
        var cType = req.ContentType;
        if (cType is null)
            return Results.BadRequest("missing content-type header");
        var contents = new Object(cType, req.Body);
        var result = await DoSubmitMediaCreationAsync(filename, contents, auth, repo, cache, logger, token);
        return result.Match(insertedName => Results.Created((string?)null, insertedName),
            FailureExtensions.AsResult);
    }

    private static Task<Stats> GetStatsForNameAsync(
        string name, HttpContext ctx, AppDbContext repo, IFusionCache cache, CancellationToken token)
    {
        var tags = ctx.TryGetTags() ?? [];
        var cToken = ctx.RequireConcurrencyToken();
        var perms = Post.RepositoryExtensionsSharedHelpers.StringListToTags(tags);
        return DoGetManagePageForNameAndPermissionAsync(name, perms, cToken, repo, cache, token);
    }

    private static async Task<IResult> RenameMediaEntryAsync(
        string name, IManageCommand.Rename renameCommand, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo,
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var result = await DoSubmitRenameForNameAsync(name, uid, renameCommand, cToken,
            repo, cache, logger, token);
        return result.Match(_ => Results.NoContent(),
            FailureExtensions.AsResult);
    }

    private static async Task<IResult> ChangeTagsForNameAsync(
        string name, IManageCommand.SetTags tagsCommand, ClaimsPrincipal auth, HttpContext ctx,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var result = await DoSubmitChangeTagsForNameAsync(name, uid, tagsCommand, cToken,
            repo, cache, logger, token);
        return result.Match(FailureExtensions.AsResult,
            Results.NoContent);
    } 
    
    private static async Task<IResult> ChangeAuthorForNameAsync(
        string name, IManageCommand.SetAuthor authorCommand, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, 
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var isPublic = ctx.TryGetTags()?.Contains(Post.RepositoryExtensions.TAG_PUBLIC) ?? false;
        var result = await DoSubmitSetAuthorForNameAsync(name, uid, isPublic, authorCommand, cToken,
            repo, cache, logger, token);
        return result.Match(_ => Results.NoContent(),
            FailureExtensions.AsResult);
    } 
    
    private static async Task<IResult> DeleteMediaEntryAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache, 
        ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var isPublic = ctx.TryGetTags()?.Contains(Post.RepositoryExtensions.TAG_PUBLIC) ?? false;
        return await DoDeleteMediumAsync(name, isPublic, uid, cToken, repo, cache, logger, token)
            .Match(FailureExtensions.AsResult,
                Results.NoContent);
    }

    extension(HttpRequest req)
    {
        private string? GetFilenameFromContentDisposition()
        {
            var disposition = req.GetTypedHeaders().ContentDisposition;
            var filenameSegment = 
                disposition?.FileNameStar.TakeIf(s => s.HasValue) 
                ?? disposition?.FileName.TakeIf(s => s.HasValue);
            return filenameSegment?.Let(HeaderUtilities.RemoveQuotes).ToString();
        }
    }
}
