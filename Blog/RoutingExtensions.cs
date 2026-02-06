using System.Globalization;
using System.Net.Mime;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

namespace CsSsg.Blog;

internal static class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddBlogRoutes()
        {
            app.MapGet("/blog/{name}",
                async (string name, ClaimsPrincipal? auth, IContentSource source, MarkdownHandler md,
                    IFusionCache cache, CancellationToken ct) =>
                {
                    Guid? uidFromCookie = null;
                    var canAccess = await cache.GetOrSetAsync(
                        $"access/{uidFromCookie}/{name}",
                        async _ => await source.GetPermissionsForContentAsync(uidFromCookie, name, ct),
                        tags: ["access"], token: ct);
                    switch (canAccess)
                    {
                        case null:
                            return TypedResults.NotFound();
                        case IContentSource.AccessLevel.None:
                            return TypedResults.Forbid();
                        case IContentSource.AccessLevel.Read or IContentSource.AccessLevel.Write:
                            var contents = await cache.GetOrSetAsync(
                                $"html/{name}", async _ =>
                                {
                                    var contents = await source.GetContentAsync(uidFromCookie, name, ct);
                                    // it could fail due to permissions but because we're caching the step after
                                    // the access check, it's going to be a lookup failure as if resource doesn't exist
                                    if (contents is null)
                                        return null;
                                    var (title, article) = md.RenderMarkdownToHtml(contents.Value, name, ct);
                                    return HtmlRenderer.ConvertHtmlArticleContentsToFullPage(title, article,
                                        addEditPostButton: canAccess == IContentSource.AccessLevel.Write);
                                },
                                tags: ["html"],
                                token: ct);
                            return contents is not null
                                ? Results.Text(contents, MediaTypeNames.Text.Html, contentEncoding: Encoding.UTF8)
                                : TypedResults.NotFound();
                        default:
                            throw new ArgumentOutOfRangeException(nameof(canAccess), canAccess, null);
                    }
                });
            app.MapGet("/blog",
                async (ClaimsPrincipal? auth, IContentSource source, IFusionCache cache,
                    CancellationToken ct, [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null) =>
                {
                    Guid? uidFromCookie = null;
                    var date = beforeOrAt is null ? DateTime.UtcNow : DateTime.Parse(beforeOrAt, 
                        null, DateTimeStyles.RoundtripKind);
                    var listing = await cache.GetOrSetAsync(
                        $"listing+html/{uidFromCookie};{date};limit",
                        async _ => HtmlRenderer.RenderPostListingToHtmlBodyElements(
                                await source.GetAvailableContentAsync(uidFromCookie, date, limit, ct),
                                addNewPostButton: uidFromCookie != null),
                        tags: ["listing", "html"], token: ct);
                    var page = HtmlRenderer.ConvertHtmlContentsToFullPage("Posts", listing);
                    return Results.Text(page, MediaTypeNames.Text.Html, Encoding.UTF8);
                });
            app.MapGet("/", () => Results.Redirect("/blog/index"));
            app.MapGet("/contact", () => Results.Redirect("/blog/contact"));
        }
    }
}