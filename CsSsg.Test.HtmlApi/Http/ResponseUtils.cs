using CsSsg.Test.HtmlApi.Html;
using Microsoft.AspNetCore.Http;

namespace CsSsg.Test.HtmlApi.Http;

internal static class ResponseUtils
{
    extension(HttpResponseMessage res)
    {
        public string? TryGetSessionCookie()
        {
            try
            {
                return res.Headers.GetValues("set-cookie")
                    .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"));
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}