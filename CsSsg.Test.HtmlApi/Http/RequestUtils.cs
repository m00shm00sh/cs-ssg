using Microsoft.AspNetCore.Http;

namespace CsSsg.Test.HtmlApi.Http;

internal static class RequestUtils
{
    extension(HttpContent req)
    {
        public HttpContent WithHeaders(HeaderDictionary headers)
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
        public HttpRequestMessage WithHeaders(HeaderDictionary headers)
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
        public Task<HttpResponseMessage> GetWithHeadersAsync(string requestUri, HeaderDictionary headers)
            => GetWithHeadersAsync(client, requestUri, headers, CancellationToken.None);

        public Task<HttpResponseMessage> GetWithHeadersAsync(string requestUri, HeaderDictionary headers,
            CancellationToken token)
            => client.SendAsync(requestUri.AsGetRequest().WithHeaders(headers), token);

        public Task<HttpResponseMessage> PostFormAsync(string requestUri, 
            IEnumerable<KeyValuePair<string, string>> form)
            => PostFormAsync(client, requestUri, form, CancellationToken.None);
        
        public Task<HttpResponseMessage> PostFormAsync(string requestUri, 
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token)
            => PostFormAsync(client, requestUri, new HeaderDictionary(), form, token);
        
        public Task<HttpResponseMessage> PostFormAsync(string requestUri, HeaderDictionary headers,
            IEnumerable<KeyValuePair<string, string>> form)
            => PostFormAsync(client, requestUri, headers, form, CancellationToken.None);
        
        public Task<HttpResponseMessage> PostFormAsync(string requestUri, HeaderDictionary headers,
            IEnumerable<KeyValuePair<string, string>> form, CancellationToken token)
            => client.PostAsync(requestUri, new FormUrlEncodedContent(form).WithHeaders(headers), token);
        
        public Task<HttpResponseMessage> PostHeadersAsync(string requestUri, HeaderDictionary headers)
            => PostHeadersAsync(client, requestUri, headers, CancellationToken.None);
        
        public Task<HttpResponseMessage> PostHeadersAsync(string requestUri, HeaderDictionary headers, 
            CancellationToken token)
            => client.PostAsync(requestUri, new ByteArrayContent([]).WithHeaders(headers), token);
    }

    extension(string uri)
    {
        private HttpRequestMessage AsGetRequest()
        => new HttpRequestMessage(HttpMethod.Get, uri);
    }
}