using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using static CsSsg.Src.Post.FilterConfigurationExtensions;
using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.Post;

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
                .AddContentAccessPermissionsFilter();

            apiGroup.MapPut(BLOG_PREFIX + NAME_SLUG, SubmitBlogEntryEditForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX + NEW_SLUG, SubmitBlogEntryCreationAsync)
                .UseJwtBearerAuthentication()
                .AddWritePermissionsFilter();

            apiGroup.MapGet(BLOG_PREFIX + NAME_SLUG + STATS_SUFFIX, GetStatsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + RENAME_SUFFIX, RenameBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + PERMISSIONS_SUFFIX, ChangePermissionsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + CHANGE_AUTHOR_SUFFIX, ChangeAuthorForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
            
            apiGroup.MapDelete(BLOG_PREFIX + NAME_SLUG, DeleteBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();
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
        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        var result = await DoSubmitBlogEntryEditForNameAsync(name, uidFromAuth, contents, isPublic, repo, cache,
            logger, token);
        return result.Match(FailureExtensions.AsResult,
            Results.NoContent);
    }

    private static async Task<IResult> SubmitBlogEntryCreationAsync(
        Contents content, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uid = auth.RequireUid;
        var result = await DoSubmitBlogEntryCreationAsync(content, uid, repo, cache, logger, token);
        return await result.MatchAsync(async insertedName =>
            {
                // if the insert didn't have a dot in it, it's not from an on-conflict-rename, meaning that it
                // could've come from after a failed update which set the access cache; clear the access entry to be
                // safe of that case
                if (!insertedName.Contains('.'))
                    await ContentAccessPermissionFilter.InvalidateAccessCacheForKeyAsync(logger, cache, 
                        ContentAccessFilterConfig, "insert", uid, insertedName, token);
                return Results.Created((string?)null, insertedName);
            },
            FailureExtensions.AsResult);
    }

    private static Task<IManageCommand.Stats> GetStatsForNameAsync(
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

    private static async Task<IResult> RenameBlogEntryAsync(
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
        var isPublic = ctx.TryGetAccessLevel() == AccessLevel.WritePublic;
        return await DoDeleteBlogEntryAsync(name, isPublic, uidFromAuth, repo, cache, logger, token)
            .Match(FailureExtensions.AsResult,
                Results.NoContent);
    }
}
