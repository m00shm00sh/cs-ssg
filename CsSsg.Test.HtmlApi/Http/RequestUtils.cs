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
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token = default)
            => PostFormAsync(client, requestUri, new HeaderDictionary(), form, token);
        
        public Task<HttpResponseMessage> PostFormAsync(string requestUri, IHeaderDictionary headers,
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token = default)
            => client.PostAsync(requestUri, new FormUrlEncodedContent(form).WithHeaders(headers), token);
        
        public Task<HttpResponseMessage> PostHeadersAsync(string requestUri, IHeaderDictionary headers, 
            CancellationToken token = default)
            => client.PostAsync(requestUri, new ByteArrayContent([]).WithHeaders(headers), token);
        
        public Task<HttpResponseMessage> PostEmptyAsync(string requestUri, CancellationToken token = default)
            => PostHeadersAsync(client, requestUri, new HeaderDictionary(), token);
        
        public Task<HttpResponseMessage> PostProtectedFormAsync(string getUri, string postUri,
            IEnumerable<KeyValuePair<string, string>> form, bool skipCsrf = false, CancellationToken token = default)
            => PostProtectedFormAsync(client, getUri, postUri, new HeaderDictionary(), form, skipCsrf, token);
        
        public async Task<HttpResponseMessage> PostProtectedFormAsync(string getUri, string postUri,
            IHeaderDictionary sharedHeaders, IEnumerable<KeyValuePair<string, string>> formPairs,
            bool skipCsrf = false, CancellationToken token = default)
        {
            var response = await client.GetWithHeadersAsync(getUri, sharedHeaders, token);
            if (!response.IsSuccessStatusCode)
                return response;
            var existingCookiesSV = sharedHeaders.Cookie;
            if (existingCookiesSV.Count > 1)
                throw new ArgumentException(
                    "multiple cookies are detected but StringValues would merge them with comma not semicolon");
            var existingCookie = (string?)existingCookiesSV;

            var (doc, antiforgery) = await response.ParseAntiforgeryForm(sharedHeaders,
                skipCsrf ? null : "__RequestVerificationToken", token);
            
            // use the interface type to give access to IHeaderDictionary extension method used later 
            IHeaderDictionary postHeaders = new HeaderDictionary(sharedHeaders.ToDictionary());
            var formData = formPairs.ToList();
            foreach (var (k, v) in formData)
            {
                var node = doc.DocumentNode.SelectSingleNode($"//form//*[@name='{k}']");
                if (node is null)
                    throw new ArgumentException($"could not find a matching input for form key {k}");
            }
            if (!skipCsrf)
                formData.Add(new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery?.RequestToken!));
            if (antiforgery?.CookieToken is not null)
                postHeaders.Cookie = existingCookie is null
                    ? antiforgery.CookieToken
                    : string.Join("; ", antiforgery.CookieToken, existingCookie);
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
    public static readonly Dictionary<string, string> EMPTY_FORM = new();

    extension(string uri)
    {
        private HttpRequestMessage AsGetRequest()
        => new HttpRequestMessage(HttpMethod.Get, uri);

        public string AsFormSubmitSelector()
            => FORM_SUBMIT_PREFIX + uri;
    }
}