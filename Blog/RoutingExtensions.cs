using System.Globalization;
using System.Net.Mime;
using System.Security.Claims;
using System.Text;
using CsSsg.Slices;
using CsSsg.Slices.ViewModels;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
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
                async Task<Results<RazorSliceHttpResult<BlogEntry>, ForbidHttpResult, NotFound>>
                (string name, ClaimsPrincipal? auth, IContentSource source, MarkdownHandler md,
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
                                        return ((string, string)?)null;
                                    return md.RenderMarkdownToHtmlUncached(contents.Value, name, ct);
                                },
                                tags: ["html"],
                                token: ct);
                            var editPage = (canAccess == IContentSource.AccessLevel.Write) ? "/blog.edit/{name}" : null;
                            return contents is var (title, article)
                                ? Results.Extensions.RazorSlice<BlogEntryView, BlogEntry>(
                                    new BlogEntry(title, new HtmlString(article), editPage))
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
                        $"listing/{uidFromCookie};{date};limit",
                        _ => source.GetAvailableContentAsync(uidFromCookie, date, limit, ct),
                        tags: ["listing"], token: ct);
                    return Results.Extensions.RazorSlice<BlogListing, Listing>(
                        new Listing(listing.Select(e =>
                                new ListingEntry(e.Title, $"/blog/{e.Name}", e.LastModified, false)
                            ), uidFromCookie is not null)
                    );
                });
            app.MapGet("/", () => Results.Redirect("/blog/index"));
            app.MapGet("/contact", () => Results.Redirect("/blog/contact"));
        }
    }
}