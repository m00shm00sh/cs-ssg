using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    private const string STATS_SUFFIX = "/stats";
    private const string RENAME_SUFFIX = "/rename";
    private const string PERMISSIONS_SUFFIX = "/permissions";
    private const string CHANGE_AUTHOR_SUFFIX = "/chauthor";
    
    extension(WebApplication app)
    {
        private void AddBlogJsonRoutes(string apiPrefix)
        {
            var apiGroup = app.MapGroup(apiPrefix);
            
            apiGroup.MapGet(BLOG_PREFIX, GetAllAvailableBlogEntriesAsync)
                .UseJwtBearerAuthentication()
                .AllowAnonymous();
            
            apiGroup.MapGet(BLOG_PREFIX + NAME_SLUG, GetBlogEntryContentsForNameAsync)
                .UseJwtBearerAuthentication()
                .AllowAnonymous()
                .AddEndpointFilter<ContentAccessPermissionFilter>();

            apiGroup.MapPut(BLOG_PREFIX + NAME_SLUG, SubmitBlogEntryEditForNameAsync)
                .UseJwtBearerAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();

            apiGroup.MapPost(BLOG_PREFIX + NEW_SLUG, SubmitBlogEntryCreationAsync)
                .UseJwtBearerAuthentication()
                .AddEndpointFilter<WritePermissionFilter>();

            apiGroup.MapGet(BLOG_PREFIX + NAME_SLUG + STATS_SUFFIX, GetStatsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
            
            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + RENAME_SUFFIX, RenameBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
            
            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + PERMISSIONS_SUFFIX, ChangePermissionsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
            
            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + CHANGE_AUTHOR_SUFFIX, ChangeAuthorForNameAsync)
                .UseJwtBearerAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
            
            apiGroup.MapDelete(BLOG_PREFIX + NAME_SLUG, DeleteBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddEndpointFilter<ContentAccessPermissionFilter>()
                .AddEndpointFilter<WritePermissionFilter>();
        }
    }

    private static async Task<Results<Ok<Contents>, NotFound>>
    GetBlogEntryContentsForNameAsync(string name, HttpContext ctx, ClaimsPrincipal? auth, AppDbContext repo,
        IFusionCache cache, CancellationToken token)
    {
        var uidFromAuth = auth?.TrySubjectUid;
        var contents = await _fetchMarkdownAsync(cache, repo, uidFromAuth, name, token);
        
        // unwrap from monad to nullable so that we get the desired type inference
        return contents.ToNullable() is {} c
            ? TypedResults.Ok(c)
            : TypedResults.NotFound();
    }

    private static async Task<IResult> SubmitBlogEntryEditForNameAsync(string name, Contents contents, HttpContext ctx,
        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var result = await DoSubmitBlogEntryEditForNameAsync(name, uidFromAuth, contents, isPublic, repo, cache,
            logger, token);
        return result.Match(AsResult,
            Results.NoContent);
    }

    private static async Task<IResult> SubmitBlogEntryCreationAsync(
        Contents content, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uid = auth.RequireUid;
        var result = await DoSubmitBlogEntryCreationAsync(content, uid, repo, cache, logger, token);
        return await result.MatchAsync(AsResult,
            async insertedName =>
            {
                // if the insert didn't have a dot in it, it's not from an on-conflict-rename, meaning that it
                // could've come from after a failed update which set the access cache; clear the access entry to be
                // safe of that case
                if (!insertedName.Contains('.'))
                    await ContentAccessPermissionFilter.InvalidateAccessCacheForKeyAsync(logger, cache, "insert", 
                        uid, insertedName, token);
                return Results.Created((string?)null, insertedName);
            });
    }

    private static Task<ManageCommand.Stats> GetStatsForNameAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var initiallyPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var perms = new ManageCommand.Permissions
        {
            Public = initiallyPublic
        };
        return DoGetManagePageForNameAndPermissionAsync(name, uidFromAuth, perms, repo, cache, token);
    }

    private static async Task<IResult> RenameBlogEntryAsync(
        string name, ManageCommand.Rename renameCommand, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var result = await DoSubmitRenameForNameAsync(name, uidFromAuth, renameCommand, 
            repo, cache, logger, token);
        return result.Match(AsResult,
            _ => Results.NoContent());
    }

    private static async Task<IResult> ChangePermissionsForNameAsync(
        string name, ManageCommand.SetPermissions permissionsCommand, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
        ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var result = await DoSubmitChangePermissionsForNameAsync(name, uidFromAuth, permissionsCommand, 
            repo, cache, logger, token);
        return result.Match(AsResult,
            Results.NoContent);
    } 
    
    private static async Task<IResult> ChangeAuthorForNameAsync(
        string name, ManageCommand.SetAuthor authorCommand, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, 
        IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var result = await DoSubmitSetAuthorForNameAsync(name, uidFromAuth, isPublic, authorCommand,
            repo, cache, logger, token);
        return result.Match(AsResult,
            _ => Results.NoContent());
    } 
    
    private static async Task<List<Entry>> GetAllAvailableBlogEntriesAsync(
        ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache, CancellationToken token,
        [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null)
    {
        var uidFromAuth = auth?.TrySubjectUid;
        var date = beforeOrAt is null
            ? DateTime.UtcNow
            : DateTime.Parse(beforeOrAt, null, DateTimeStyles.RoundtripKind);
        var entries = await DoGetAllAvailableBlogEntriesAsync(uidFromAuth, limit, date, repo, cache, token);
        return entries.ToList();
    }

    private static async Task<IResult> DeleteBlogEntryAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache, 
        ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        return await DoDeleteBlogEntryAsync(name, isPublic, uidFromAuth, repo, cache, logger, token)
            .Match(AsResult,
                Results.NoContent);
    }
}
