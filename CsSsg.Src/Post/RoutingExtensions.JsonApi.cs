using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.Post;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    private const string STATS_SUFFIX = "/stats";
    private const string RENAME_SUFFIX = "/rename";
    private const string TAGS_SUFFIX = "/tags";
    private const string CHANGE_AUTHOR_SUFFIX = "/chauthor";
    
    extension(WebApplication app)
    {
        private void AddBlogJsonRoutes(string apiPrefix)
        {
            var apiGroup = app.MapGroup(apiPrefix);
            
            apiGroup.MapGet(BLOG_PREFIX,
                    TryExtractUidFromOptionalClaimsThenInvokeGetAllAvailableBlogEntriesThenTransformResultAsync(
                        (listing, _) => listing.ToList()))
                .UseJwtBearerAuthentication()
                .AllowAnonymous();
            
            apiGroup.MapGet(BLOG_PREFIX + NAME_SLUG, GetBlogEntryContentsForNameAsync)
                .UseJwtBearerAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter()
                .AddIfModifiedSinceFilter();

            apiGroup.MapPut(BLOG_PREFIX + NAME_SLUG, SubmitBlogEntryEditForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX, SubmitBlogEntryCreationAsync)
                .UseJwtBearerAuthentication()
                .AddWritePermissionsFilter();

            apiGroup.MapGet(BLOG_PREFIX + NAME_SLUG + STATS_SUFFIX, GetStatsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + RENAME_SUFFIX, RenameBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + TAGS_SUFFIX, ChangeTagsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + CHANGE_AUTHOR_SUFFIX, ChangeAuthorForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
            
            apiGroup.MapDelete(BLOG_PREFIX + NAME_SLUG, DeleteBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter();
        }
    }

    private static async Task<Results<Ok<Contents>, NotFound>>
    GetBlogEntryContentsForNameAsync(string name, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        CancellationToken token, int revision = 0)
    {
        var cToken = ctx.RequireConcurrencyToken();
        // unwrap from monad to nullable so that we get the desired type inference
        var contents = (await _fetchMarkdownAsync(cache, repo, name, cToken, token, revision)).ToNullable();

        if (contents is not null)
        {
            #nullable disable
            ctx.SetModifiedSinceValue(contents?.LastModified);
            return TypedResults.Ok(contents.Value);
            #nullable enable
        }
        return TypedResults.NotFound();
    }

    private static async Task<IResult> SubmitBlogEntryEditForNameAsync(string name, Contents contents, HttpContext ctx,
        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var isPublic = ctx.TryGetTags()?.Contains(Post.RepositoryExtensions.TAG_PUBLIC) ?? false;
        var result = await DoSubmitBlogEntryEditForNameAsync(name, uid, contents, isPublic, cToken,
            repo, cache, logger, token);
        return result.Match(FailureExtensions.AsResult,
            Results.NoContent);
    }

    private static async Task<IResult> SubmitBlogEntryCreationAsync(
        Contents content, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, ILogger<Routing> logger,
        CancellationToken token)
    {
        var uid = auth.RequireUid();
        var result = await DoSubmitBlogEntryCreationAsync(content, uid, repo, cache, logger, token);
        return result.Match(insertResult => Results.Created((string?)null, insertResult),
            FailureExtensions.AsResult);
    }

    private static Task<IManageCommand.Stats> GetStatsForNameAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var tags = ctx.TryGetTags() ?? [];
        var perms = RepositoryExtensionsSharedHelpers.StringListToTags(tags);
        return DoGetManagePageForNameAndPermissionAsync(name, uid, perms, cToken, repo, cache, token);
    }

    private static async Task<IResult> RenameBlogEntryAsync(
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
        var isPublic = ctx.TryGetTags()?.Contains(RepositoryExtensions.TAG_PUBLIC) ?? false;
        var result = await DoSubmitSetAuthorForNameAsync(name, uid, isPublic, authorCommand, cToken,
            repo, cache, logger, token);
        return result.Match(_ => Results.NoContent(),
            FailureExtensions.AsResult);
    } 
    
    private static async Task<IResult> DeleteBlogEntryAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache, 
        ILogger<Routing> logger, CancellationToken token)
    {
        var uid = auth.RequireUid();
        var cToken = ctx.RequireConcurrencyToken();
        var isPublic = ctx.TryGetTags()?.Contains(RepositoryExtensions.TAG_PUBLIC) ?? false;
        return await DoDeleteBlogEntryAsync(name, isPublic, uid, cToken, repo, cache, logger, token)
            .Match(FailureExtensions.AsResult,
                Results.NoContent);
    }
}
