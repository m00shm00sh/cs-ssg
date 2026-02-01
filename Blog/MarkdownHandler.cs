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

    private MarkdownDocument _getMarkdown(string markdown, string tag, CancellationToken ct)
    => cache.GetOrSet($"markdown/{tag}", factory: _ => Markdown.Parse(markdown, _pipeline),
        tags: ["markdown"], token: ct);

    public string? GetMarkdownTitle(string markdown, string tag, CancellationToken ct)
        => cache.GetOrSet($"title/{tag}", factory: _ =>
            _getMarkdown(markdown, tag, ct)
                .AsQueryable()
                .GetHeadings(1)
                .Select(n => n.Value)
                .FirstOrDefault(t => !string.IsNullOrEmpty(t)),
            tags: ["title"], token: ct
        );
    
    public (string?, string) RenderMarkdownToHtml(string markdown, string tag, CancellationToken ct)
    {
        var md = _getMarkdown(markdown, tag, ct);
        var docArticle = md.ToHtml(_pipeline);
        var title = GetMarkdownTitle(markdown, tag, ct);
        return (title, docArticle);
    }
}