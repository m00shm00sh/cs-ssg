using HtmlAgilityPack;

namespace CsSsg.Test.HtmlApi.Html;

internal static class Matchers
{
    extension(HtmlNode node)
    {

        public bool MatchesAttributes(params (string, string)[] attributes)
        {
            foreach (var (k, v) in attributes)
            {
                if (node.Attributes[k].Value != v)
                    return false;
            }

            return true;
        }
    }

    // typing helper for lambda type deduction
    public static Action<HtmlDocument> DocumentMatcher(Action<HtmlDocument> action)
        => action;
}