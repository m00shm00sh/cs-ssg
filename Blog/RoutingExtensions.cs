using System.Net.Mime;
using System.Text;
using ZiggyCreatures.Caching.Fusion;

namespace CsSsg.Blog;

internal static class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddBlogRoutes()
        {
            app.MapGet("/blog/{name}",
                async (string name, IContentSource source, IFusionCache cache,
                    CancellationToken ct) =>
                {
                    var contents = await cache.GetOrSetAsync(
                        name, async _ =>
                        {
                            var contents = await source.GetContentOrNullAsync(name, ct);
                            return contents is null ? null : MarkdownToHtml.RenderMarkdownToHtml(contents);
                        },
                        token: ct);
                    return contents is not null
                        ? Results.Text(contents, MediaTypeNames.Text.Html, contentEncoding: Encoding.UTF8)
                        : TypedResults.NotFound();
                });
            
            app.MapGet("/", () => Results.Redirect("/blog/index"));
            app.MapGet("/contact", () => Results.Redirect("/blog/contact"));
        }
    }
}