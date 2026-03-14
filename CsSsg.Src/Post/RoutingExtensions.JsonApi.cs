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
            
            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + MANAGE_SUFFIX, SubmitManageEntryForNameAsync)
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
        var result = await DoSubmitBlogEntryEditForNameAsync(name, uidFromAuth, contents, isPublic, true, repo, cache,
            logger, token);
        return result.Match(
            failCode => failCode.AsResult,
            Results.NoContent);
    }

    private static async Task<IResult> SubmitBlogEntryCreationAsync(
        Contents content, [FromServices] ContentAccessPermissionFilter contentFilter, ClaimsPrincipal auth,
        AppDbContext repo, IFusionCache cache, ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid;
        var result = await DoSubmitBlogEntryCreationAsync(content, uid, false, repo, cache, logger, token);
        return await result.MatchAsync(
            failCode => failCode.AsResult,
            async insertedName =>
            {
                // if the insert didn't have a dot in it, it's not from an on-conflict-rename, meaning that it
                // could've come from after a failed update which set the access cache; clear the access entry to be
                // safe of that case
                if (!insertedName.Contains('.'))
                    await contentFilter.InvalidateAccessCacheForKeyAsync("insert", uid, insertedName, token);
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
        return DoGetManagePageForNameAsync(name, uidFromAuth, perms, repo, cache, token);
    }

    private static async Task<IResult /* 400 | (transitive: 403 | 404) | 204 */> SubmitManageEntryForNameAsync(
        string name, ManageCommand command, ClaimsPrincipal auth, HttpContext ctx,
        AppDbContext repo, IFusionCache cache, IAntiforgery aft, ILogger<Routing> logger, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var initiallyPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        var contentFilter = ctx.Features.Get<ContentAccessPermissionFilter>()
            ?? throw new InvalidOperationException("couldn't find content filter instance");
        var manageResult = await DoSubmitManageEntryPageForNameAsync(name, uidFromAuth, initiallyPublic, command,
            contentFilter, repo, cache, logger, token);
        if (manageResult is RedirectHttpResult) // DoSubmitManageXxx returns a Redirect on success, which is useless here
            return Results.NoContent();
        return manageResult;
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
        string name, ClaimsPrincipal auth, HttpContext ctx, ILogger<Routing> logger, AppDbContext repo,
        ContentAccessPermissionFilter contentFilter, IFusionCache cache, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        var isPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
        return await DoDeleteBlogEntryAsync(name, isPublic, uidFromAuth, logger, repo, contentFilter, cache, token)
            .Match(
                failCode => failCode.AsResult,
                Results.NoContent
            );
    }
}
