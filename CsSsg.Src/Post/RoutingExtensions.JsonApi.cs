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
                .AddIfModifiedSinceFilter()
                .Produces(StatusCodes.Status304NotModified)
                .Produces(StatusCodes.Status403Forbidden);

            apiGroup.MapPut(BLOG_PREFIX + NAME_SLUG, SubmitBlogEntryEditForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWritePermissionsFilter()
                .Produces(StatusCodes.Status204NoContent)
                .AnnotateProducesFailure()
                .Produces(StatusCodes.Status401Unauthorized);

            apiGroup.MapPost(BLOG_PREFIX, SubmitBlogEntryCreationAsync)
                .UseJwtBearerAuthentication()
                .AddWritePermissionsFilter()
                .Produces<string>(StatusCodes.Status201Created)
                .AnnotateProducesFailure()
                .Produces(StatusCodes.Status401Unauthorized);

            apiGroup.MapGet(BLOG_PREFIX + NAME_SLUG + STATS_SUFFIX, GetStatsForNameAsync)
                .UseJwtBearerAuthentication()
                .AllowAnonymous()
                .AddContentAccessPermissionsFilter()
                .Produces(StatusCodes.Status403Forbidden)
                .Produces(StatusCodes.Status404NotFound);

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + RENAME_SUFFIX, RenameBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter()
                .Produces(StatusCodes.Status204NoContent)
                .AnnotateProducesFailure()
                .Produces(StatusCodes.Status401Unauthorized);

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + TAGS_SUFFIX, ChangeTagsForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter()
                .Produces(StatusCodes.Status204NoContent)
                .AnnotateProducesFailure()
                .Produces(StatusCodes.Status401Unauthorized);

            apiGroup.MapPost(BLOG_PREFIX + NAME_SLUG + CHANGE_AUTHOR_SUFFIX, ChangeAuthorForNameAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter()
                .Produces(StatusCodes.Status204NoContent)
                .AnnotateProducesFailure()
                .Produces(StatusCodes.Status401Unauthorized);
            
            apiGroup.MapDelete(BLOG_PREFIX + NAME_SLUG, DeleteBlogEntryAsync)
                .UseJwtBearerAuthentication()
                .AddContentAccessPermissionsFilter()
                .AddWriteMetadataPermissionsFilter()
                .Produces(StatusCodes.Status204NoContent)
                .AnnotateProducesFailure()
                .Produces(StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<Results<Ok<Contents>, NotFound, Conflict>>
    GetBlogEntryContentsForNameAsync(string name, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        CancellationToken token, int revision = 0)
    {
        var cToken = ctx.RequireConcurrencyToken();
        var contents = await FetchMarkdownAsync(name, cToken, repo, cache, token, revision);
        return contents.Match<Results<Ok<Contents>, NotFound, Conflict>>(
            content =>
            {
                ctx.SetModifiedSinceValue(content.LastModified);
                return TypedResults.Ok(content);
            },
            failure => failure == Failure.Conflict
                ? TypedResults.Conflict()
                : TypedResults.NotFound());
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

    private static async Task<IResult> GetStatsForNameAsync(
        string name, ClaimsPrincipal auth, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        CancellationToken token)
    {
        var cToken = ctx.RequireConcurrencyToken();
        var tags = ctx.TryGetTags() ?? [];
        var perms = RepositoryExtensionsSharedHelpers.StringListToTags(tags);
        return (await DoGetManagePageForNameAndPermissionAsync(name, perms, cToken, repo, cache, token))
            .Match<IResult>(stats => TypedResults.Ok(stats), FailureExtensions.AsResult);
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
