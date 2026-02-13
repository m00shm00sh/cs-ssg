using System.Globalization;
using System.Security.Claims;
using CsSsg.Auth;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Db;
using CsSsg.Post;
using CsSsg.Slices;
using CsSsg.Slices.ViewModels;

namespace CsSsg.Blog;

internal static class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddBlogRoutes()
        {
            app.MapGet("/blog/{name}",
                async Task<Results<RazorSliceHttpResult<BlogEntry>, ForbidHttpResult, NotFound>>
                (string name, ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache, CancellationToken token) =>
                {
                    Guid? uidFromCookie = auth?.TryUid;
                    var canAccess = await cache.GetOrSetAsync(
                        $"access/{uidFromCookie}/{name}",
                        async _ => await repo.GetPermissionsForContentAsync(uidFromCookie, name, token),
                        tags: ["access"], token: token);
                    switch (canAccess)
                    {
                        case null:
                            return TypedResults.NotFound();
                        case AccessLevel.None:
                            return TypedResults.Forbid();
                        case AccessLevel.Read or AccessLevel.Write:
                            var contents = await cache.GetOrSetAsync(
                                $"html.body/{name}", async _ =>
                                {
                                    var contents = await repo.GetContentAsync(uidFromCookie, name, token);
                                    return contents.Match(
                                        (Contents c) => (c.Title, MarkdownHandler.RenderMarkdownToHtmlArticle(c.Body)),
                                        (Failure f) => ((string, string)?)null
                                    );
                                },
                                tags: ["html"],
                                token: token);
                            var editPage = (canAccess == AccessLevel.Write) ? "/blog.edit/{name}" : null;
                            return contents is var (title, article)
                                ? Results.Extensions.RazorSlice<BlogEntryView, BlogEntry>(
                                    new BlogEntry(title, new HtmlString(article), editPage))
                                : TypedResults.NotFound();
                        default:
                            throw new ArgumentOutOfRangeException(nameof(canAccess), canAccess, null);
                    }
                });
            app.MapGet("/blog",
                async (ClaimsPrincipal? auth, AppDbContext repo, IFusionCache cache,
                    CancellationToken token, [FromQuery] int limit = 10, [FromQuery] string? beforeOrAt = null) =>
                {
                    Guid? uidFromCookie = auth.TryUid;
                    var date = beforeOrAt is null ? DateTime.UtcNow : DateTime.Parse(beforeOrAt, 
                        null, DateTimeStyles.RoundtripKind);
                    var listing = await cache.GetOrSetAsync(
                        $"listing/{uidFromCookie};{date};limit",
                        _ => repo.GetAvailableContentAsync(uidFromCookie, date, limit, token),
                        tags: ["listing"], token: token);
                    return Results.Extensions.RazorSlice<BlogListing, Listing>(
                        new Listing(listing.Select(e =>
                                new ListingEntry(e.Title, $"/blog/{e.Slug}", e.LastModified, false)
                            ), uidFromCookie is not null)
                    );
                });
            app.MapGet("/", () => Results.Redirect("/blog/index"));
            app.MapGet("/contact", () => Results.Redirect("/blog/contact"));
        }
    }
}