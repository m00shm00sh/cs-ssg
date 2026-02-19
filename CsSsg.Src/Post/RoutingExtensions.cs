using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Blog;
using CsSsg.Src.Db;
using CsSsg.Src.Slices;
using CsSsg.Src.Slices.ViewModels;

namespace CsSsg.Src.Post;

internal static class RoutingExtensions
{
    // also used by User.RoutingExtensions
    internal const string BLOG_PREFIX = "/blog";
    private const string RX_SLUG_WITH_OPT_UUID = @"^\w+(-\w+)*(\.[[0-9a-f]]{{32}})?$";
    [StringSyntax("Route")] private const string NAME_SLUG = $"/{{name:regex({RX_SLUG_WITH_OPT_UUID})}}";
    
    private const string EDIT_SUFFIX = "/edit";
    private const string SUBMIT_EDIT_SUFFIX = "/edit.1";
    // for ContentAccessPermissionFilter
    internal static readonly HashSet<string> WRITE_ENDPOINTS =
    [
        EDIT_SUFFIX[1..], SUBMIT_EDIT_SUFFIX[1..]
    ];

    private static class CacheHelpers
    {
        internal static string HtmlBodyKey(string name)
            => $"html.body/{name}";
        internal static string MarkdownContentsKey(string name)
            => $"md/{name}";

        internal static string ListingKey(Guid? uid, DateTime dateUtc, int limit)
        {
            Debug.Assert(dateUtc.ToUniversalTime() == dateUtc, "datetime is not in utc format");
            return $"listing/{uid};{dateUtc};{limit}";
        }
        
        internal static readonly string[] HtmlBodyTags = ["html"];
        internal static readonly string[] MarkdownContentTags = ["md"];
        internal static List<string> ListingTags(Guid? uid, bool isPublic)
        {
            List<string> tags = [];
            if (isPublic) tags.Add("listing");
            if (uid is not null) tags.Add($"listing/{uid}");
            return tags;
        }
    }

    extension(WebApplication app)
    {
        public void AddBlogRoutes()
        {
            app.MapGet(BLOG_PREFIX + NAME_SLUG,
                    async Task<Results<RazorSliceHttpResult<BlogEntry>, NotFound>>
                    (string name, HttpContext ctx, ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache,
                        CancellationToken token) =>
                    {
                        var uidFromCookie = auth?.TryUid;
                        var contents = await cache.GetOrSetAsync(
                            CacheHelpers.HtmlBodyKey(name), async _ =>
                            {
                                var contents = await _fetchMarkdown(cache, repo, uidFromCookie, name, token);
                                return contents?.RenderHtml();
                            },
                            tags: CacheHelpers.HtmlBodyTags, token: token);
                        var hasWritePermission = ctx.Features.Get<PostPermission>()?.AccessLevel.IsWrite is not null;

                        var editPage = hasWritePermission ? $"{BLOG_PREFIX}/{name}{EDIT_SUFFIX}" : null;
                        return contents is var (title, article)
                            ? Results.Extensions.RazorSlice<BlogEntryView, BlogEntry>(
                                new BlogEntry(title, new HtmlString(article), editPage))
                            : TypedResults.NotFound();
                    })
                .AddEndpointFilter<ContentAccessPermissionFilter>();

            app.MapGet(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX,
                    Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>>
                    (string name, HttpContext ctx, ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache,
                        IAntiforgery af, CancellationToken token) =>
                    {
                        var uidFromCookie = auth.TryUid!.Value;
                        return _renderEditPage(name, uidFromCookie, null, ctx, repo, cache, af, token);
                    })
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + EDIT_SUFFIX,
                    Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>>
                    (string name, [FromForm] string title, [FromForm] string contents, HttpContext ctx,
                        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af,
                        CancellationToken token) =>
                    {
                        var uidFromCookie = auth.TryUid!.Value;
                        var formContents = new Contents(title, contents);
                        return _renderEditPage(name, uidFromCookie, formContents, ctx, repo, cache, af, token);
                    })
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>();

            app.MapPost(BLOG_PREFIX + NAME_SLUG + SUBMIT_EDIT_SUFFIX,
                    async Task<IResult>
                    (string name, [FromForm] string title, [FromForm] string contents, HttpContext ctx,
                        ClaimsPrincipal auth, AppDbContext repo, IFusionCache cache, IAntiforgery af,
                        ILogger<Routing> logger, CancellationToken token) =>
                    {
                        var uidFromCookie = auth.TryUid!.Value;
                        var isPublic = ctx.Features.Get<PostPermission>()?.AccessLevel == AccessLevel.WritePublic;
                        var cEntry = new Contents(title, contents);
                        if (await repo.UpdateContentAsync(uidFromCookie, name, cEntry, token) is { } f)
                            return f.AsResult;
                        var article = MarkdownHandler.RenderMarkdownToHtmlArticle(contents);
                        await _setCacheEntries(cache, name, cEntry, article, token);
                        RoutingLogging.LogUpdateSlugNameInvalidateCachesByUidAndPublic(logger, name, uidFromCookie, isPublic);
                        await cache.RemoveByTagAsync(CacheHelpers.ListingTags(uidFromCookie, isPublic), token: token);
                        return TypedResults.Redirect(BLOG_PREFIX + $"/{name}");
                    })
                .AddEndpointFilter<RequireUidEndpointFilter>()
                .AddEndpointFilter<ContentAccessPermissionFilter>();

            app.MapGet(BLOG_PREFIX,
                async (ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache,
                    CancellationToken token, [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null) =>
                {
                    var uidFromCookie = auth.TryUid;
                    var date = beforeOrAt is null
                        ? DateTime.UtcNow
                        : DateTime.Parse(beforeOrAt,
                            null, DateTimeStyles.RoundtripKind);
                    var listing = await cache.GetOrSetAsync(
                        CacheHelpers.ListingKey(uidFromCookie, date, limit),
                        _ => repo.GetAvailableContentAsync(uidFromCookie, date, limit, token),
                        tags: CacheHelpers.ListingTags(uidFromCookie, true), token: token);
                    return Results.Extensions.RazorSlice<BlogListing, Listing>(
                        new Listing(listing.Select(e =>
                            new ListingEntry(e.Title, $"{BLOG_PREFIX}/{e.Slug}", e.LastModified,
                                CanDeleteOrMove: e.AccessLevel == AccessLevel.Write)
                        ), uidFromCookie is not null)
                    );
                });

            app.MapGet("/", () => Results.Redirect(BLOG_PREFIX));
            app.MapGet("/contact", () => Results.Redirect($"{BLOG_PREFIX}/contact"));
        }

    }

    private static async Task<Contents?> _fetchMarkdown(IFusionCache cache, AppDbContext repo, Guid? userId,
        string name, CancellationToken token)
        => await cache.GetOrSetAsync(CacheHelpers.MarkdownContentsKey(name), async _ =>
        {
            var contents = await repo.GetContentAsync(userId, name, token);
            return contents.Match(
                (Contents c) => c,
                (Failure f) => (Contents?)null
            );
        }, tags: CacheHelpers.MarkdownContentTags, token: token);

    private static async Task _setCacheEntries(IFusionCache cache, string name, Contents contents,
        string markdownHtml, CancellationToken token)
    {
        await cache.SetAsync(CacheHelpers.HtmlBodyKey(name), contents with { Body = markdownHtml },
            tags: CacheHelpers.HtmlBodyTags, token: token);
        await cache.SetAsync(CacheHelpers.MarkdownContentsKey(name), contents,
            tags: CacheHelpers.MarkdownContentTags, token: token);
    }

    // unify the handling for both GET and POST:
    // if both formTitle and formContents are null then GET endpoint was matched and we fetch from cache;
    // if neither are null then POST was matched and use contents. The handler lambda is responsible for CSRF validation
    private static async Task<Results<NotFound, RazorSliceHttpResult<BlogEntryEdit>>> _renderEditPage(
        string nameSlug, Guid userId, Contents? formData, HttpContext ctx, AppDbContext repo, IFusionCache cache,
        IAntiforgery af, CancellationToken token)
    {
        var contents = formData ?? await _fetchMarkdown(cache, repo, userId, nameSlug, token);
        if (contents is null)
            return TypedResults.NotFound();
        var htmlContents = contents.Value.RenderHtml();
        var aft = af.GetAndStoreTokens(ctx);
        return Results.Extensions.RazorSlice<BlogEntryEditView, BlogEntryEdit>(
            new BlogEntryEdit(new HtmlString(htmlContents.Body), 
                contents.Value.Title, contents.Value.Body,
                $"{BLOG_PREFIX}/{nameSlug}{EDIT_SUFFIX}",
                $"{BLOG_PREFIX}/{nameSlug}{SUBMIT_EDIT_SUFFIX}", aft));
    }

    extension(Contents contents)
    {
        private Contents RenderHtml() => contents with
        {
            Body = MarkdownHandler.RenderMarkdownToHtmlArticle(contents.Body)
        };
    }

    extension(Failure f)
    {
        private IResult AsResult => f switch
        {
            Failure.NotFound =>
                Results.NotFound(),
            Failure.NotPermitted =>
                Results.Forbid(),
            Failure.Conflict or
            Failure.TooLong => 
                // a Results.UnprocessableEntity would also do here since it's a validation failure
                Results.BadRequest(),
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, null)
        };
    }
}

internal static partial class RoutingLogging
{
    [LoggerMessage(LogLevel.Debug, "updater: slug {name}: invalidate cache: uid={uid} public={isPublic}")]
    internal static partial void LogUpdateSlugNameInvalidateCachesByUidAndPublic(ILogger<Routing> logger, string name, Guid uid, bool isPublic);
}

internal abstract class Routing;

// not a file class because that breaks the logging codegen
internal partial class ContentAccessPermissionFilter(
    ILogger<ContentAccessPermissionFilter> Logger, IFusionCache Cache, AppDbContext Repo
    ) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var uid = http.User?.TryUid;
        if (http.GetRouteValue("name") is not string name)
            throw new InvalidOperationException("unexpected: could not find route param \"name\" having type string");
        var token = http.RequestAborted;

        // ReSharper disable once NullableWarningSuppressionIsUsed
        var isWriteEndpoint = RoutingExtensions.WRITE_ENDPOINTS.Contains(http.Request.Path.Value?.Split('/').Last()!);
        
        LogContentAccessPermissionsInvocation(Logger, name, isWriteEndpoint, uid);
        
        if (isWriteEndpoint && uid is null)
            throw new InvalidOperationException("write endpoint detected but no logged-in uid present");
        
        var canAccess = await Cache.GetOrSetAsync(
            $"access/{uid}/{name}",
            async _ => await Repo.GetPermissionsForContentAsync(uid, name, token),
            tags: ["access"], token: token);
        switch (canAccess)
        {
            case null:
                return Results.NotFound();
            case AccessLevel.None:
            case AccessLevel.Read when isWriteEndpoint:
                return Results.Forbid();
            case AccessLevel.Read:
            case AccessLevel.Write /* isWriteEndpoint invariant */:
            case AccessLevel.WritePublic /* isWriteEndpoint invariant */:
                break;
            default:
                Debug.Assert(false, $"unexpected permission value: {canAccess}");
                return Results.InternalServerError("unexpected permission value");
        }
        http.Features.Set(new PostPermission(canAccess.Value));
        return await next(context);
    }

    [LoggerMessage(LogLevel.Debug, "content access permissions: name={name}, isWrite={isWrite}, uid={uid}")]
    static partial void LogContentAccessPermissionsInvocation(ILogger<ContentAccessPermissionFilter> logger, string name, bool isWrite, Guid? uid);
}

// class instead of readonly struct so that it can be nullable
file record PostPermission(AccessLevel AccessLevel);