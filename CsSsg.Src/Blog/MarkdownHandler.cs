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

    /// <summary>
    /// Renders Markdown to HTML. The choice of Markdown engine is an implementation detail
    /// not exposed through this function.
    /// </summary>
    /// <param name="contents">Markdown string</param>
    /// <returns>HTML string</returns>
    public static string RenderMarkdownToHtmlArticle(string contents)
    {
        var md = Markdown.Parse(contents, _pipeline);
        var docArticle = md.ToHtml(_pipeline);
        return docArticle;
    }
}
