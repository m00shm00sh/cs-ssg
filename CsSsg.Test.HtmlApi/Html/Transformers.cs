using HtmlAgilityPack;
using Microsoft.AspNetCore.Http;

namespace CsSsg.Test.HtmlApi.Html;

internal static class Transformers
{
    extension(FormUrlEncodedContent form)
    {
        public FormUrlEncodedContent WithHeaders(HeaderDictionary headers)
        {
            foreach (var header in headers)
            {
                form.Headers.Remove(header.Key);
                form.Headers.Add(header.Key, header.Value.ToArray());
            }
            return form;
        }
    }
}