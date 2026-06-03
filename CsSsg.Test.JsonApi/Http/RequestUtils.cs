using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

using MObject = CsSsg.Src.Media.Object;

namespace CsSsg.Test.JsonApi.Http;

internal static class RequestUtils
{
    internal record GetOptions(string? Bearer = null, DateTimeOffset? IfModifiedSince = null);
    
    extension(HttpRequestMessage req)
    {
        private HttpRequestMessage WithBearer(string? bearer)
        {
            if (!string.IsNullOrWhiteSpace(bearer))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return req;
        }

        private HttpRequestMessage WithOptions(GetOptions? options)
        {
            if (options is null)
                return req;
            req.WithBearer(options.Bearer);
            if (options.IfModifiedSince is not null)
                req.Headers.IfModifiedSince = options.IfModifiedSince;
            return req;
        }

        private HttpRequestMessage WithContent(HttpContent content)
        {
            req.Content = content;
            return req;
        }
    }

    extension(HttpContent content)
    {
        private HttpContent WithContentType(string contentType)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return content;
        }
        
        private HttpContent WithContentDisposition(string filename)
        {
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = filename
            };
            return content;
        }
    }

    extension(HttpClient client)
    {
        public Task<HttpResponseMessage> ApiGetWithOptionsAsync(string requestUri, GetOptions? options = null,
            IEnumerable<(string, string)>? queryBuilder = null, CancellationToken token = default)
            => client.SendAsync(requestUri.WithQuery(queryBuilder).AsApiGetRequest().WithOptions(options), token);

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
            JsonSerializerOptions? options = null, CancellationToken token = default)
            => ApiPostJsonWithBearerAsync(client, requestUri, null, value, options, token);

        public Task<HttpResponseMessage> ApiPostJsonWithBearerAsync<T>(string requestUri, string? bearer, T value,
            JsonSerializerOptions? options = null, CancellationToken token = default)
        {
            var request = requestUri.AsApiPostRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            request.WithContent(JsonContent.Create(value, mediaType: null, options ?? JSON_OPTIONS));
            return client.SendAsync(request, token);
        }
        
        public Task<HttpResponseMessage> ApiPutJsonAsync<T>(string requestUri, T value,
            JsonSerializerOptions? options = null, CancellationToken token = default)
            => ApiPutJsonWithBearerAsync(client, requestUri, null, value, options, token);
        
        public Task<HttpResponseMessage> ApiPutJsonWithBearerAsync<T>(string requestUri, string? bearer, T value,
            JsonSerializerOptions? options = null, CancellationToken token = default)
        {
            var request = requestUri.AsApiPutRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            request.WithContent(JsonContent.Create(value, mediaType: null, options ?? JSON_OPTIONS));
            return client.SendAsync(request, token);
        }

        public Task<HttpResponseMessage> ApiPostFileAsync(string requestUri, string filename, MObject data,
            CancellationToken token = default)
            => ApiPostFileWithBearerAsync(client, requestUri, null, filename, data, token);

        public Task<HttpResponseMessage> ApiPostFileWithBearerAsync(string requestUri, string? bearer,
            string filename, MObject data, CancellationToken token = default)
        {
            var request = requestUri.AsApiPostRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            var content = new StreamContent(data.ContentStream);
            if (!string.IsNullOrWhiteSpace(data.ContentType))
                content.WithContentType(data.ContentType);
            if (!string.IsNullOrWhiteSpace(filename))
                content.WithContentDisposition(filename);
            request.WithContent(content);
            return client.SendAsync(request, token);
        }
        
        public Task<HttpResponseMessage> ApiPutFileAsync(string requestUri, MObject data, 
            CancellationToken token = default)
            => ApiPutFileWithBearerAsync(client, requestUri, null, data, token);
        
        public Task<HttpResponseMessage> ApiPutFileWithBearerAsync(string requestUri, string? bearer, MObject data,
            CancellationToken token = default)
        {
            var request = requestUri.AsApiPutRequest();
            if (bearer != null)
                request.WithBearer(bearer);
            var content = new StreamContent(data.ContentStream);
            if (!string.IsNullOrWhiteSpace(data.ContentType))
                content.WithContentType(data.ContentType);
            request.WithContent(content);
            return client.SendAsync(request, token);
        }
    }
   
    extension(string uri)
    {
        private string WithQuery(IEnumerable<(string, string)>? query)
            => query is not null
                ? uri + "?" + string.Join("&", query.Select(kv => $"{kv.Item1}={WebUtility.UrlEncode(kv.Item2)}"))
                : uri;
                
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