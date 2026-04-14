using HtmlAgilityPack;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

using CsSsg.Test.HtmlApi.Html;
using KotlinScopeFunctions;

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
                    .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
                    ?.Let(s => s.Split(';')[0]);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }    
        
        public async Task<(HtmlDocument, AntiforgeryTokenSet?)> ParseAntiforgeryForm(
            string? formField = "__RequestVerificationToken", CancellationToken token = default)
        {
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException("cannot parse non-success response");
            
            string? cookieAntiforgery = null;
            try // Headers.GetValues throws on missing instead of returning null so exception-wrap it
            {
                cookieAntiforgery = res.Headers.GetValues("set-cookie")
                    .FirstOrDefault(s => s.StartsWith(".AspNetCore.Antiforgery"))?.Split(';')?[0];
            }
            catch (InvalidOperationException) { }
            var doc = Loaders.LoadHtml(await res.Content.ReadAsStringAsync(token));
            if (formField is null)
                return (doc, null);
            var formToken = doc.DocumentNode
                .SelectSingleNode($"//form//input[@type='hidden' and @name='{formField}']")
                .Attributes["value"].Value;
            var antiforgeryToken = new AntiforgeryTokenSet(formToken, cookieAntiforgery, formField, null);
            return (doc, antiforgeryToken);
        }
    }
}