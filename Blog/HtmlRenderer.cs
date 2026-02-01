using HarroDotNet.HtmlBuilder;
using static HarroDotNet.HtmlBuilder.CommonAttributes;

namespace CsSsg.Blog;

internal static class HtmlRenderer
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

    private readonly struct TextAsRawHtmlRenderer(string html) : IContentRenderer
    {
        public void Render(Action<string> append)
            => append(html);
    }

    public static IContentRenderer[] RenderPostListingToHtmlBodyElements(IEnumerable<IContentSource.Entry> entries,
        bool addNewPostButton)
    {
        var list = new List<IContentRenderer>
        {
            new H1
            {
                "Latest posts"
            },
            new Main
            {
                new Article
                {
                    new Ul
                    {
                        entries.Select(entry =>
                            new Li
                            {
                                new Section
                                {
                                    new A(Href($"/blog/{entry.Name}"))
                                    {
                                        new H3
                                        {
                                            entry.Title
                                        },
                                    },
                                    $"Last modified: {entry.LastModified:yyyy-MM-dd HH:mm:ss}"
                                }
                            }
                        )
                    }
                }
            }
        };
        if (addNewPostButton)
            ;
        return list.ToArray();
    }

    public static string ConvertHtmlArticleContentsToFullPage(string? title, string articleHtml, bool addEditPostButton)
    {
        var contents = new List<IContentRenderer>
        {
            new Main
            {
                new Article
                {
                    new TextAsRawHtmlRenderer(articleHtml)
                }
            }
        };
        if (addEditPostButton)
            ;
        return ConvertHtmlContentsToFullPage(title, contents.ToArray()); }

    public static string ConvertHtmlContentsToFullPage(string? title, params IContentRenderer[] bodyRenderers)
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
                    bodyRenderers
                }
            }
        ).Render();
}