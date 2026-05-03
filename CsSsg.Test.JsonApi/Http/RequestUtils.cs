using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MObject = CsSsg.Src.Media.Object;

namespace CsSsg.Test.JsonApi.Http;

internal static class RequestUtils
{
    extension(HttpRequestMessage req)
    {
        public HttpRequestMessage WithBearer(string bearer)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return req;
        }

        public HttpRequestMessage WithContent(HttpContent content)
        {
            req.Content = content;
            return req;
        }
    }

    extension(HttpContent content)
    {
        public HttpContent WithContentType(string contentType)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return content;
        }
        
        public HttpContent WithContentDisposition(string filename)
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