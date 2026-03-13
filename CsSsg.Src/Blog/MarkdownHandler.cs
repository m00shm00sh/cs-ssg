using Markdig;

namespace CsSsg.Src.Blog;

internal static class MarkdownHandler
{
    // ReSharper disable once InconsistentNaming
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

    public static string RenderMarkdownToHtmlArticle(string contents)
    {
        var md = Markdown.Parse(contents, _pipeline);
        var docArticle = md.ToHtml(_pipeline);
        return docArticle;
    }
}
