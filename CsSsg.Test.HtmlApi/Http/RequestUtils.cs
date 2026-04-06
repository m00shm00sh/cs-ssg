using CsSsg.Test.HtmlApi.Html;
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
    }

    extension(HttpClient client)
    {
        public Task<HttpResponseMessage> GetWithHeadersAsync(string requestUri, IHeaderDictionary headers)
            => GetWithHeadersAsync(client, requestUri, headers, CancellationToken.None);

        public Task<HttpResponseMessage> GetWithHeadersAsync(string requestUri, IHeaderDictionary headers,
            CancellationToken token)
            => client.SendAsync(requestUri.AsGetRequest().WithHeaders(headers), token);

        public Task<HttpResponseMessage> PostFormAsync(string requestUri, 
            IEnumerable<KeyValuePair<string, string>> form)
            => PostFormAsync(client, requestUri, form, CancellationToken.None);
        
        public Task<HttpResponseMessage> PostFormAsync(string requestUri, 
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token)
            => PostFormAsync(client, requestUri, new HeaderDictionary(), form, token);
        
        public Task<HttpResponseMessage> PostFormAsync(string requestUri, IHeaderDictionary headers,
            IEnumerable<KeyValuePair<string, string>> form)
            => PostFormAsync(client, requestUri, headers, form, CancellationToken.None);
        
        public Task<HttpResponseMessage> PostFormAsync(string requestUri, IHeaderDictionary headers,
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token)
            => client.PostAsync(requestUri, new FormUrlEncodedContent(form).WithHeaders(headers), token);
        
        public Task<HttpResponseMessage> PostHeadersAsync(string requestUri, IHeaderDictionary headers)
            => PostHeadersAsync(client, requestUri, headers, CancellationToken.None);
        
        public Task<HttpResponseMessage> PostHeadersAsync(string requestUri, IHeaderDictionary headers, 
            CancellationToken token)
            => client.PostAsync(requestUri, new ByteArrayContent([]).WithHeaders(headers), token);
        
        public Task<HttpResponseMessage> PostEmptyAsync(string requestUri)
            => PostEmptyAsync(client, requestUri, CancellationToken.None);
        
        public Task<HttpResponseMessage> PostEmptyAsync(string requestUri, CancellationToken token)
            => PostHeadersAsync(client, requestUri, new HeaderDictionary(), token);
        
        public Task<HttpResponseMessage> PostProtectedFormAsync(string getUri, string postUri,
            IEnumerable<KeyValuePair<string, string>> form, bool skipCsrf = false)
            => PostProtectedFormAsync(client, getUri, postUri, new HeaderDictionary(), form, skipCsrf);
        
        public Task<HttpResponseMessage> PostProtectedFormAsync(string getUri, string postUri,
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token, bool skipCsrf = false)
            => PostProtectedFormAsync(client, getUri, postUri, new HeaderDictionary(), form, token, skipCsrf);
        
        public Task<HttpResponseMessage> PostProtectedFormAsync(string getUri, string postUri,
            IHeaderDictionary sharedHeaders, IEnumerable<KeyValuePair<string, string>> form, bool skipCsrf = false)
            => PostProtectedFormAsync(client, getUri, postUri, sharedHeaders, form, CancellationToken.None, skipCsrf);

        public async Task<HttpResponseMessage> PostProtectedFormAsync(
            string getUri, string postUri, IHeaderDictionary sharedHeaders,
            IEnumerable<KeyValuePair<string, string>> formPairs, CancellationToken token, bool skipCsrf = false)
        {
            var response = await client.GetWithHeadersAsync(getUri, sharedHeaders, token);
            response.EnsureSuccessStatusCode();
            var existingCookiesSV = sharedHeaders.Cookie;
            if (existingCookiesSV.Count > 1)
                throw new ArgumentException(
                    "multiple cookies are detected but StringValues would merge them with comma not semicolon");
            var existingCookie = (string?)existingCookiesSV;
            
            string? cookieAntiforgery = null;
            try // Headers.GetValues throws on missing instead of returning null so exception-wrap it
            {
                if (!skipCsrf)
                    cookieAntiforgery = response.Headers.GetValues("set-cookie")
                        .FirstOrDefault(s => s.StartsWith(".AspNetCore.Antiforgery"))?.Split(';')?[0];
            }
            catch (InvalidOperationException) { }
            var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync(token));
            var formToken = !skipCsrf
                ? doc.DocumentNode
                    .SelectSingleNode("//form//input[@type='hidden' and @name='__RequestVerificationToken']")
                    .Attributes["value"].Value
                : null;
            // use the interface type to give access to IHeaderDictionary extension method used later 
            IHeaderDictionary postHeaders = new HeaderDictionary(sharedHeaders.ToDictionary());
            var formData = formPairs.ToList();
            foreach (var (k, v) in formData)
            {
                var node = doc.DocumentNode.SelectSingleNode($"//form//input[@name='{k}']");
                if (node is null)
                    throw new ArgumentException($"could not find a matching input for form key {k}");
            }
            if (!skipCsrf)
                formData.Add(new KeyValuePair<string, string>("__RequestVerificationToken", formToken!));
            if (cookieAntiforgery is not null && !skipCsrf)
                postHeaders.Cookie = existingCookie is null
                    ? cookieAntiforgery
                    : string.Join("; ", cookieAntiforgery, existingCookie);
            if (postUri.StartsWith(FORM_SUBMIT_PREFIX))
            {
                var selector = postUri.Substring(FORM_SUBMIT_PREFIX.Length).Split('=', 2);
                var submitNode = doc.DocumentNode.SelectSingleNode(
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

    extension(string uri)
    {
        private HttpRequestMessage AsGetRequest()
        => new HttpRequestMessage(HttpMethod.Get, uri);

        public string AsFormSubmitSelector()
            => FORM_SUBMIT_PREFIX + uri;
    }
}