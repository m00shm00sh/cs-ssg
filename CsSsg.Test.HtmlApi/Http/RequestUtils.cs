using HtmlAgilityPack;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

using MObject = CsSsg.Src.Media.Object;

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

        public HttpContent WithContentType(string contentType)
            => req.WithHeaders(new HeaderDictionary
                {
                    ["Content-type"] = contentType
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

    internal interface IMultipartEntry
    {
        void AppendToMultipartForm(MultipartFormDataContent form);
    }

    internal record struct MultipartString(string Value) : IMultipartEntry
    {
        public void AppendToMultipartForm(MultipartFormDataContent form)
            => form.Add(new StringContent(Value));
    }

    internal record struct MultipartFile(string Filename, MObject Object) : IMultipartEntry
    {
        public void AppendToMultipartForm(MultipartFormDataContent form)
            => form.Add(new StreamContent(Object.ContentStream).WithContentType(Object.ContentType), Filename);
    }
    
    private delegate Task<HttpResponseMessage> FormPoster<TFormItem>(HttpClient client, string postUri,
        IHeaderDictionary headers,
        IEnumerable<KeyValuePair<string, TFormItem>> formPairs, CancellationToken token = default);
    
    private static FormPoster<IMultipartEntry> PostMultipartFormAsync_WithBoundary(string multipartBoundary)
        => (client, uri, headers, pairs, token) 
            => client.PostMultipartFormAsync(uri, headers, pairs, multipartBoundary, token);

    private delegate KeyValuePair<string, TFormItem> FormItemGenerator<TFormItem>(string name, string value);

    extension(HttpClient client)
    {
        public Task<HttpResponseMessage> GetWithCookieAsync(string requestUri, string cookie,
            CancellationToken token = default)
            => client.SendAsync(requestUri.AsGetRequest().WithCookie(cookie), token);

        public Task<HttpResponseMessage> PostFormAsync(string requestUri, IHeaderDictionary headers,
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token = default)
            => client.PostAsync(requestUri, new FormUrlEncodedContent(form).WithHeaders(headers), token);

        public Task<HttpResponseMessage> PostMultipartFormAsync(string requestUri, IHeaderDictionary headers,
            IEnumerable<KeyValuePair<string, IMultipartEntry>> multipartForm, string multipartBoundary = "",
            CancellationToken token = default)
        {
            var form = string.IsNullOrWhiteSpace(multipartBoundary)
                    ? new MultipartFormDataContent()
                    : new MultipartFormDataContent(multipartBoundary);
            form.WithHeaders(headers);
            foreach (var kvp in multipartForm)
                kvp.Value.AppendToMultipartForm(form);
            return client.PostAsync(requestUri, form, token);
        }
        
       
        public Task<HttpResponseMessage> PostCookieAsync(string requestUri, string cookie,
            CancellationToken token = default)
            => client.PostAsync(requestUri, new ByteArrayContent([]).WithCookie(cookie), token);
        
        public Task<HttpResponseMessage> PostEmptyAsync(string requestUri, CancellationToken token = default)
            => client.PostAsync(requestUri, new ByteArrayContent([]), token);

        public Task<HttpResponseMessage> PostProtectedFormAsync(string getUri, string postUri,
            IEnumerable<KeyValuePair<string, string>> formPairs, string? sessionCookie = null, bool skipCsrf = false,
            CancellationToken token = default)
            => client.DoPostProtectedAnyFormAsync(getUri, postUri, formPairs, PostFormAsync, _formPair, sessionCookie,
                skipCsrf, token);
        
        public Task<HttpResponseMessage> PostProtectedMultipartFormAsync(string getUri, string postUri,
            IEnumerable<KeyValuePair<string, IMultipartEntry>> formPairs, string? sessionCookie = null, 
            bool skipCsrf = false, string mpBoundary = "", CancellationToken token = default)
            => client.DoPostProtectedAnyFormAsync(getUri, postUri, formPairs, 
                PostMultipartFormAsync_WithBoundary(mpBoundary), _mpFormPair, sessionCookie,
                skipCsrf, token);
        
        private async Task<HttpResponseMessage> DoPostProtectedAnyFormAsync<TFormItem>(string getUri, string postUri,
            IEnumerable<KeyValuePair<string, TFormItem>> formPairs, FormPoster<TFormItem> formPoster,
            FormItemGenerator<TFormItem> formItemGenerator, string? sessionCookie = null, bool skipCsrf = false,
            CancellationToken token = default)
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

            return await client.DoPostProtectedAnyFormAsync(doc, antiforgery, postUri, formPairs, formPoster,
                formItemGenerator, sessionCookie, skipCsrf, token);
        }

       
        public Task<HttpResponseMessage> PostProtectedFormAsync(HtmlDocument formDoc,
            AntiforgeryTokenSet? antiforgery, string postUri,
            IEnumerable<KeyValuePair<string, string>> formPairs, string? sessionCookie = null,
            bool skipCsrf = false, CancellationToken token = default)
            => client.DoPostProtectedAnyFormAsync(formDoc, antiforgery, postUri, formPairs, PostFormAsync, _formPair,
                sessionCookie, skipCsrf, token);
        
        public Task<HttpResponseMessage> PostProtectedMultipartFormAsync(HtmlDocument formDoc,
            AntiforgeryTokenSet? antiforgery, string postUri,
            IEnumerable<KeyValuePair<string, IMultipartEntry>> formPairs, string? sessionCookie = null,
            bool skipCsrf = false, string multipartBoundary = "", CancellationToken token = default)
            => client.DoPostProtectedAnyFormAsync(formDoc, antiforgery, postUri, formPairs,
                PostMultipartFormAsync_WithBoundary(multipartBoundary), _mpFormPair, sessionCookie, skipCsrf, token);
        
        
        private Task<HttpResponseMessage> DoPostProtectedAnyFormAsync<TFormItem>(HtmlDocument formDoc, 
            AntiforgeryTokenSet? antiforgery, string postUri, IEnumerable<KeyValuePair<string, TFormItem>> formPairs,
            FormPoster<TFormItem> formPoster, FormItemGenerator<TFormItem> formItemGenerator,
            string? sessionCookie = null, bool skipCsrf = false, CancellationToken token = default)
        {
            if (!skipCsrf && antiforgery is null)
                throw new ArgumentException("antiforgery is null without skipCsrf", nameof(antiforgery));
            // use the interface type to give access to IHeaderDictionary extension method used later 
            IHeaderDictionary postHeaders = new HeaderDictionary();
            var formData = formPairs.ToList();
            foreach (var (k, _) in formData)
            {
                var node = formDoc.DocumentNode.SelectSingleNode($"//form//*[@name='{k}']");
                if (node is null)
                    throw new ArgumentException($"could not find a matching input for form key {k}");
            }
            if (!skipCsrf)
                formData.Add(formItemGenerator("__RequestVerificationToken", antiforgery?.RequestToken!));
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
            return formPoster(client, postUri, postHeaders, formData, token);
        }
    }
   
    private const string FORM_SUBMIT_PREFIX = "form-submit:";
    public static readonly Dictionary<string, string> EMPTY_FORM = new();
    
    private static KeyValuePair<string, string> _formPair(string k, string v) 
        => new(k, v);
    private static KeyValuePair<string, IMultipartEntry> _mpFormPair(string k, string v) 
        => new(k, new MultipartString(v));

    extension(string uri)
    {
        private HttpRequestMessage AsGetRequest()
        => new HttpRequestMessage(HttpMethod.Get, uri);

        public string AsFormSubmitSelector()
            => FORM_SUBMIT_PREFIX + uri;
    }
}