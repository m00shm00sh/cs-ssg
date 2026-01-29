using HarroDotNet.HtmlBuilder;
using static HarroDotNet.HtmlBuilder.CommonAttributes;

namespace CsSsg.Blog;

internal static class HtmlDocumentMaker
{
    extension(Element elem)
    {
        private Element AddIf(bool condition, Func<IContentRenderer> node)
        {
            if (condition)
                elem.Add(node());
            return elem;
        }
    }

    private readonly struct TextAsRawHtmlRender(string html) : IContentRenderer
    {
        public void Render(Action<string> append)
            => append(html);
    }
    
    public static string ConvertHtmlContentsToFullPage(string? title, string html)
        => new Document(
            new Html
            {
                new Head
                {
                    new Meta(Charset.Utf8),
                    new Meta(Name("viewport"), Content("width=device-width, initial-scale=1")),
                    new Link(Href("/s/index.css"), Rel.Stylesheet),
                }.AddIf(title is not null, () =>
                    new Title
                    {
                        title!
                    }
                ),
                new Body
                {
                    new Main
                    {
                        new Article
                        {
                            new TextAsRawHtmlRender(html)
                        }
                    }
                }
            }
        ).Render();
}