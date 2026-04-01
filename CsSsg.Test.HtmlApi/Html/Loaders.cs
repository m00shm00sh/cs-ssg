using HtmlAgilityPack;

namespace CsSsg.Test.HtmlApi.Html;

internal static class Loaders
{
    public static HtmlDocument LoadHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }
}