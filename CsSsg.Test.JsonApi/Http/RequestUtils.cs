using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using MObject = CsSsg.Src.Media.Object;

namespace CsSsg.Test.JsonApi.Http;

internal static class RequestUtils
{
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
        
        public HttpRequestMessage WithBearer(string bearer)
            => req.WithHeaders(new HeaderDictionary()
            {
                ["Authorization"] = $"Bearer {bearer}"
            });

        public HttpRequestMessage WithContent(HttpContent content)
        {
            req.Content = content;
            return req;
        }
    }

    extension(HttpClient client)
    {
        public Task<HttpResponseMessage> ApiGetWithBearerAsync(string requestUri, string bearer, 
            CancellationToken token = default)
            => client.SendAsync(requestUri.AsApiGetRequest().WithBearer(bearer), token);
        
        public Task<HttpResponseMessage> ApiGetAsync(string requestUri, CancellationToken token = default)
            => client.SendAsync(requestUri.AsApiGetRequest(), token);

        public Task<HttpResponseMessage> ApiDeleteWithBearerAsync(string requestUri, string bearer,
            CancellationToken token = default)
            => client.SendAsync(requestUri.AsApiDeleteRequest().WithBearer(bearer), token);
        
        public Task<HttpResponseMessage> ApiDeleteAsync(string requestUri, CancellationToken token = default)
            => client.SendAsync(requestUri.AsApiDeleteRequest(), token);

        public Task<HttpResponseMessage> ApiPostEmptyWithBearerAsync(string requestUri, string bearer,
            CancellationToken token = default)
            => client.SendAsync(requestUri.AsApiPostRequest()
                    .WithBearer(bearer)
                    .WithContent(new ByteArrayContent(Array.Empty<byte>())),
                token);
        
        public Task<HttpResponseMessage> ApiPostJsonAsync<T>(string requestUri, T value,
            JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
            => ApiPostJsonWithBearerAsync(client, requestUri, null, value, options, cancellationToken);

        public Task<HttpResponseMessage> ApiPostJsonWithBearerAsync<T>(string requestUri, string? bearer, T value,
            JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = requestUri.AsApiPostRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            request.WithContent(JsonContent.Create(value, mediaType: null, options ?? JSON_OPTIONS));
            return client.SendAsync(request, cancellationToken);
        }
        
        public Task<HttpResponseMessage> ApiPutJsonAsync<T>(string requestUri, T value,
            JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        => ApiPutJsonWithBearerAsync(client, requestUri, null, value, options, cancellationToken);
        
        public Task<HttpResponseMessage> ApiPutJsonWithBearerAsync<T>(string requestUri, string? bearer, T value,
            JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = requestUri.AsApiPutRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            request.WithContent(JsonContent.Create(value, mediaType: null, options ?? JSON_OPTIONS));
            return client.SendAsync(request, cancellationToken);
        }

        public Task<HttpResponseMessage> ApiPostFileAsync(string requestUri, string filename, MObject data,
            CancellationToken cancellationToken = default)
            => ApiPostFileWithBearerAsync(client, requestUri, null, filename, data, cancellationToken);

        public Task<HttpResponseMessage> ApiPostFileWithBearerAsync(string requestUri, string? bearer,
            string filename, MObject data, CancellationToken cancellationToken = default)
        {
            var request = requestUri.AsApiPostRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            var headers = _prepareHeaders(filename, data.ContentType);
            request.WithHeaders(headers);
            request.WithContent(new StreamContent(data.ContentStream));
            return client.SendAsync(request, cancellationToken);
        }
        
        public Task<HttpResponseMessage> ApiPutFileAsync(string requestUri, string filename, MObject data, 
            CancellationToken cancellationToken = default)
            => ApiPutFileWithBearerAsync(client, requestUri, null, filename, data, cancellationToken);
        
        public Task<HttpResponseMessage> ApiPutFileWithBearerAsync(string requestUri, string? bearer, 
            string filename, MObject data, CancellationToken cancellationToken = default)
        {
            var request = requestUri.AsApiPutRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            var headers = _prepareHeaders(filename, data.ContentType);
            request.WithHeaders(headers);
            request.WithContent(new StreamContent(data.ContentStream));
            return client.SendAsync(request, cancellationToken);
        }
    }
    
    private static HeaderDictionary _prepareHeaders(string filename, string contentType)
    {
        var headers = new HeaderDictionary();
        ((IHeaderDictionary)headers).ContentType = contentType;
        var disposition = new ContentDispositionHeaderValue("inline")
        {
            FileName = filename
        };
        ((IHeaderDictionary)headers).ContentDisposition = disposition.ToString();
        return headers;
    }
   
    extension(string uri)
    {
        private HttpRequestMessage AsApiGetRequest()
            => new HttpRequestMessage(HttpMethod.Get, API_PREFIX + uri);
        private HttpRequestMessage AsApiPostRequest()
            => new HttpRequestMessage(HttpMethod.Post, API_PREFIX + uri);
        private HttpRequestMessage AsApiPutRequest()
            => new HttpRequestMessage(HttpMethod.Put, API_PREFIX + uri);
        private HttpRequestMessage AsApiDeleteRequest()
        => new HttpRequestMessage(HttpMethod.Delete, API_PREFIX + uri);
    }

    private const string API_PREFIX = "/api/v1";
    
    internal static readonly JsonSerializerOptions JSON_OPTIONS = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}