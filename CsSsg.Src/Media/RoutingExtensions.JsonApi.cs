using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using static CsSsg.Src.Media.FilterConfigurationExtensions;
using CsSsg.Src.Post;
using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.Media;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    private const string STATS_SUFFIX = "/stats";
    private const string RENAME_SUFFIX = "/rename";
    private const string PERMISSIONS_SUFFIX = "/permissions";
    private const string CHANGE_AUTHOR_SUFFIX = "/chauthor";
    
    extension(WebApplication app)
    {
        private void AddMediaJsonRoutes(string apiPrefix)
        {
            var apiGroup = app.MapGroup(apiPrefix);

            apiGroup.MapGet(MEDIA_PREFIX, ExtractUidFromClaimsThenInvokeGetAllAvailableMediaThenTransformResult(
                    entries => entries.ToList()))
                .UseJwtBearerAuthentication();

            apiGroup.MapGet(MEDIA_PREFIX + NAME_SLUG,
                    TryExtractUidFromOptionalClaimsThenInvokeDoGetMediaAsync(auth => auth?.TrySubjectUid)
                )
                .UseJwtBearerAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter();

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
                .AddWritePermissionsFilter();
            
            apiGroup.MapPost(MEDIA_PREFIX + NAME_SLUG + RENAME_SUFFIX, RenameMediaEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            apiGroup.MapPost(MEDIA_PREFIX + NAME_SLUG + PERMISSIONS_SUFFIX, ChangePermissionsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            apiGroup.MapPost(MEDIA_PREFIX + NAME_SLUG + CHANGE_AUTHOR_SUFFIX, ChangeAuthorForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            apiGroup.MapDelete(MEDIA_PREFIX + NAME_SLUG, DeleteMediaEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
        }
    }

    private static async Task<IResult> SubmitMediaEditForNameAsync(string name, HttpContext ctx, HttpRequest req,
        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var cType = req.ContentType;
        if (cType is null)
            return Results.BadRequest("missing content-type header");
        var contents = new Object(cType, req.Body);
        var result = await DoSubmitMediaEditForNameAsync(name, uidFromAuth, contents, isPublic, repo, cache,
            logger, token);
        return result.Match(FailureExtensions.AsResult,
            Results.NoContent);
    }

    private static async Task<IResult> SubmitMediaCreationAsync(HttpContext ctx, HttpRequest req, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid;
        var filename = req.GetTypedHeaders().ContentDisposition?.FileNameStar.Value;
        if (filename is null)
            return  Results.BadRequest("missing content-disposition header with filename parameter");
        var cType = req.ContentType;
        if (cType is null)
            return Results.BadRequest("missing content-type header");
        var contents = new Object(cType, req.Body);
        var result = await DoSubmitMediaCreationAsync(filename, contents, uid, repo, cache, logger, token);
        return result.Match(insertedName => Results.Created((string?)null, insertedName),
            FailureExtensions.AsResult);
    }

    private static Task<Stats> GetStatsForNameAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var initiallyPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var perms = new IManageCommand.Permissions
        {
            Public = initiallyPublic
        };
        return DoGetManagePageForNameAndPermissionAsync(name, uidFromAuth, perms, repo, cache, token);
    }

    private static async Task<IResult> RenameMediaEntryAsync(
        string name, IManageCommand.Rename renameCommand, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var result = await DoSubmitRenameForNameAsync(name, uidFromAuth, renameCommand, 
            repo, cache, logger, token);
        return result.Match(_ => Results.NoContent(),
            FailureExtensions.AsResult);
    }

    private static async Task<IResult> ChangePermissionsForNameAsync(
        string name, IManageCommand.SetPermissions permissionsCommand, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var result = await DoSubmitChangePermissionsForNameAsync(name, uidFromAuth, permissionsCommand, 
            repo, cache, logger, token);
        return result.Match(FailureExtensions.AsResult,
            Results.NoContent);
    } 
    
    private static async Task<IResult> ChangeAuthorForNameAsync(
        string name, IManageCommand.SetAuthor authorCommand, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, 
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var result = await DoSubmitSetAuthorForNameAsync(name, uidFromAuth, isPublic, authorCommand,
            repo, cache, logger, token);
        return result.Match(_ => Results.NoContent(),
            FailureExtensions.AsResult);
    } 
    
    private static async Task<IResult> DeleteMediaEntryAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache, 
        ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        return await DoDeleteMediumAsync(name, isPublic, uidFromAuth, repo, cache, logger, token)
            .Match(FailureExtensions.AsResult,
                Results.NoContent);
    }
}
