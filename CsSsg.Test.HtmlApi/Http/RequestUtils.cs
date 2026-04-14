using HtmlAgilityPack;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace CsSsg.Test.HtmlApi.Http;

internal static class RequestUtils
{
    extension(HttpContent req)
    {
        public HttpContent WithHeaders(IHeaderDictionary headers)
        {
            foreach (var header in headers)
            {
                req.Headers.Remove(header.Key);
                req.Headers.Add(header.Key, header.Value.ToArray());
            }
            return req;
        }
        public HttpContent WithCookie(string cookie)
            => req.WithHeaders(new HeaderDictionary
            {
                ["Cookie"] = cookie
            });
    }
    
    extension(HttpRequestMessage req)
    {
        public HttpRequestMessage WithHeaders(IHeaderDictionary headers)
        {
            foreach (var header in headers)
            {
                req.Headers.Remove(header.Key);
                req.Headers.Add(header.Key, header.Value.ToArray());
            }
            return req;
        }
        public HttpRequestMessage WithCookie(string cookie)
            => req.WithHeaders(new HeaderDictionary
            {
                ["Cookie"] = cookie
            });
    }

    extension(HttpClient client)
    {
        public Task<HttpResponseMessage> GetWithCookieAsync(string requestUri, string cookie,
            CancellationToken token = default)
            => client.SendAsync(requestUri.AsGetRequest().WithCookie(cookie), token);

        public Task<HttpResponseMessage> PostFormAsync(string requestUri, IHeaderDictionary headers,
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token = default)
            => client.PostAsync(requestUri, new FormUrlEncodedContent(form).WithHeaders(headers), token);
        
        public Task<HttpResponseMessage> PostCookieAsync(string requestUri, string cookie,
            CancellationToken token = default)
            => client.PostAsync(requestUri, new ByteArrayContent([]).WithCookie(cookie), token);
        
        public Task<HttpResponseMessage> PostEmptyAsync(string requestUri, CancellationToken token = default)
            => client.PostAsync(requestUri, new ByteArrayContent([]), token);
        
        public async Task<HttpResponseMessage> PostProtectedFormAsync(string getUri, string postUri,
            IEnumerable<KeyValuePair<string, string>> formPairs, string? sessionCookie = null,
            bool skipCsrf = false, CancellationToken token = default)
        {
            var response = await (
                sessionCookie != null 
                    ? client.GetWithCookieAsync(getUri, sessionCookie, token)
                    : client.GetAsync(getUri, token)
            );
            if (!response.IsSuccessStatusCode)
                return response;

            var (doc, antiforgery) = await response.ParseAntiforgeryForm(
                skipCsrf ? null : "__RequestVerificationToken", token);

            return await client.PostProtectedFormAsync(doc, antiforgery, postUri, formPairs, sessionCookie, skipCsrf, token);
        }
        
        public async Task<HttpResponseMessage> PostProtectedFormAsync(HtmlDocument formDoc, 
            AntiforgeryTokenSet? antiforgery, string postUri,
            IEnumerable<KeyValuePair<string, string>> formPairs, string? sessionCookie = null,
            bool skipCsrf = false, CancellationToken token = default)
        {
            if (!skipCsrf && antiforgery is null)
                throw new ArgumentException("antiforgery is null without skipCsrf", nameof(antiforgery));
            // use the interface type to give access to IHeaderDictionary extension method used later 
            IHeaderDictionary postHeaders = new HeaderDictionary();
            var formData = formPairs.ToList();
            foreach (var (k, v) in formData)
            {
                var node = formDoc.DocumentNode.SelectSingleNode($"//form//*[@name='{k}']");
                if (node is null)
                    throw new ArgumentException($"could not find a matching input for form key {k}");
            }
            if (!skipCsrf)
                formData.Add(new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery?.RequestToken!));
            if (sessionCookie is not null)
                postHeaders.Cookie = sessionCookie;
            if (antiforgery?.CookieToken is not null)
                postHeaders.Cookie = sessionCookie is null
                    ? antiforgery.CookieToken
                    : string.Join("; ", antiforgery.CookieToken, sessionCookie);
            if (postUri.StartsWith(FORM_SUBMIT_PREFIX))
            {
                var selector = postUri.Substring(FORM_SUBMIT_PREFIX.Length).Split('=', 2);
                var submitNode = formDoc.DocumentNode.SelectSingleNode(
                    $"//input[@type='submit' and @{selector[0]}='{selector[1]}']");
                if (submitNode is null)
                    throw new ArgumentException("could not match submitter for @{selector[0]}='{selector[1]}'");
                var action = submitNode.Attributes["formaction"]?.Value;
                if (string.IsNullOrEmpty(action))
                {
                    var parent = submitNode.ParentNode;
                    while (parent.OriginalName != "form")
                        parent = parent.ParentNode;
                    action = parent.Attributes["action"]?.Value;
                    if (string.IsNullOrEmpty(action))
                        throw new ArgumentException("no action found for either the input or its parent");
                }
                postUri = action;
            }
            return await client.PostFormAsync(postUri, postHeaders, formData, token);
        }
    }
   
    private const string FORM_SUBMIT_PREFIX = "form-submit:";
    public static readonly Dictionary<string, string> EMPTY_FORM = new();

    extension(string uri)
    {
        private HttpRequestMessage AsGetRequest()
        => new HttpRequestMessage(HttpMethod.Get, uri);

        public string AsFormSubmitSelector()
            => FORM_SUBMIT_PREFIX + uri;
    }
}