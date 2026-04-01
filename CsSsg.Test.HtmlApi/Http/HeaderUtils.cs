using Microsoft.AspNetCore.Http;

namespace CsSsg.Test.HtmlApi.Http;

internal static class HeaderUtils
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
            
    }

    extension(string uri)
    {
        private HttpRequestMessage AsGetRequest()
        => new HttpRequestMessage(HttpMethod.Get, uri);
    }
}