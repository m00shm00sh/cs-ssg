using Markdig;
using Markdig.Syntax;
using MarkdigExtensions.Query;
using ZiggyCreatures.Caching.Fusion;

namespace CsSsg.Blog;

internal class MarkdownHandler(IFusionCache cache)
{
    private static readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder()
            /* expand MarkdownPipelineBuilder.UseAdvancedExtensions() (mostly) */
            .UseAbbreviations()
            .UseAlertBlocks()
            .UseAutoLinks()
            .UseAutoIdentifiers()
            .UseCitations()
            .UseCustomContainers()
            .UseDefinitionLists()
            .UseEmphasisExtras()
            .UseFigures()
            .UseFooters()
            .UseFootnotes()
            .UseGridTables()
            .UseMathematics()
            .UseMediaLinks()
            .UsePipeTables()
            .UseListExtras()
            .UseTaskLists()
            .UseDiagrams()
            .UseGenericAttributes()
            .UseSyntaxHighlighting()
        .Build();

    public string? GetMarkdownTitle(string markdown, string tag, CancellationToken ct)
        => cache.GetOrSet($"title/{tag}",_ =>
                Markdown.Parse(markdown, _pipeline).Title,
            tags: ["title"], token: ct);

    public (string?, string) RenderMarkdownToHtml(IContentSource.Contents contents, string tag, CancellationToken ct)
        => cache.GetOrSet($"html/{tag}", _ =>
        {
            var md = Markdown.Parse(contents.Body, _pipeline);
            var docArticle = md.ToHtml(_pipeline);
            // we already have a parse so no need to hit the cache to trigger a second parse
            var title = contents.Title ?? md.Title ?? $"[Error: could not infer title for {tag}]";
            return (title, docArticle);
        }, tags: ["html"], token: ct);
}

internal static class MarkdownExtensions
{
    extension(MarkdownDocument md)
    {
        public string? Title
            => md.AsQueryable()
                .GetHeadings(1)
                .Select(n => n.Value)
                .FirstOrDefault(t => !string.IsNullOrEmpty(t));
    }
}
