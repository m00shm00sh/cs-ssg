using Markdig;
using Markdig.Parsers;
#if BOOTDEV_SSG_BASTARDIZED_PARSE
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
#endif
using MarkdigExtensions.Query;

namespace CsSsg.Blog;

internal static class MarkdownToHtml
{
#if BOOTDEV_SSG_BASTARDIZED_PARSE
    private class SsgEmphasisExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        { }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer) return;

            var emphasisRenderer = renderer.ObjectRenderers.FindExact<EmphasisInlineRenderer>();
            if (emphasisRenderer is null) return;
            var previousTag = emphasisRenderer.GetTag;
            emphasisRenderer.GetTag = (ei) =>
                (ei.DelimiterChar is '*' or '_')
                    ? ei.DelimiterCount == 2 ? "b" : "i"
                    : previousTag(ei);
        }
    }
    
    private class BlockQuoteRenderer : HtmlObjectRenderer<QuoteBlock>
    {
        protected override void Write(HtmlRenderer renderer, QuoteBlock obj)
        {
            renderer.EnsureLine();
            if (renderer.EnableHtmlForBlock)
            {
                renderer.Write("<blockquote");
                renderer.WriteAttributes(obj);
                renderer.Write('>');
            }
            var savedImplicitParagraph = renderer.ImplicitParagraph;
            renderer.ImplicitParagraph = false;
            renderer.WriteChildren(obj);
            renderer.ImplicitParagraph = savedImplicitParagraph;
            if (renderer.EnableHtmlForBlock)
            {
                renderer.WriteLine("</blockquote>");
            }
            renderer.EnsureLine(); 
        }
    }
    
    private class ParagraphRenderer : HtmlObjectRenderer<ParagraphBlock>
    {
        protected override void Write(HtmlRenderer renderer, ParagraphBlock obj)
        {
            renderer.WriteLeafInline(obj);
            renderer.EnsureLine();
        }
    }
    
    private class SsgBlockquoteFlattener : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        { }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer htmlRenderer) return;
            htmlRenderer.EnableHtmlEscape = false;
            htmlRenderer.ImplicitParagraph = true;
            htmlRenderer.ObjectRenderers.Replace<Markdig.Renderers.Html.ParagraphRenderer>(new ParagraphRenderer());
            htmlRenderer.ObjectRenderers.Replace<QuoteBlockRenderer>(new BlockQuoteRenderer());
        }
    }
#endif
    
    private static readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder()
#if BOOTDEV_SSG_BASTARDIZED_PARSE
            .Use<SsgEmphasisExtension>()
            .Use<SsgBlockquoteFlattener>()
#else
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
#endif
        .Build();

    public static (string?, string) RenderMarkdownToHtml(string markdown)
    {
        var md = MarkdownParser.Parse(markdown, _pipeline);
        var docArticle = md.ToHtml(_pipeline);
        var title = md.AsQueryable()
            .GetHeadings(1)
            .Select(n => n.Value)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));
        return (title, docArticle);
    }
}