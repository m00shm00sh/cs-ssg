using Markdig;
using Markdig.Syntax;
using MarkdigExtensions.Query;

namespace CsSsg.ConsoleLoader;

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

    public static string? InferTitleOfMarkdownViaH1(string markdown)
        => Markdown.Parse(markdown, _pipeline).Title;
}

file static class MarkdownExtensions
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
