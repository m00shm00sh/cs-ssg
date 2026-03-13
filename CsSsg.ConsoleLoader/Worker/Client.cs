using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

using CsSsg.Src.User;
using KotlinScopeFunctions;

namespace CsSsg.ConsoleLoader.Worker;

internal partial class Client(ILoggerFactory loggerFactory, Request user, string baseAddress)
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ILogger<Client> _logger = loggerFactory.CreateLogger<Client>();

    private readonly HttpClient _client = new HttpClient
    {
        BaseAddress = new Uri(baseAddress)
    }.Also(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csssg-consoleloader/0");
    });

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _jwtBearerToken;

    private async Task _lockedTask(Func<Task> func, CancellationToken token)
    {
        try
        {
            await _tokenLock.WaitAsync(token);
            await func();
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static Func<Task> _taskifyAction(Action action)
        => () =>
        {
            action();
            return Task.CompletedTask;
        };

    private async Task RefreshJwtBearerTokenAsync(CancellationToken token)
        => await _lockedTask(async () =>
        {
            LogLogin_BeginForUser(user.Email);
            var result = await _client.PostAsJsonAsync("/api/v1/user/login", user,
                JSON_OPTIONS, token);
            if (result.StatusCode == HttpStatusCode.Forbidden)
            {
                LogLogin_FailedToLoginUserByEmail(user.Email);
                throw new HttpRequestException($"could not login user {user.Email}", null, result.StatusCode);
            }

            var body = await result.Content.ReadFromJsonAsync<LoginResponse>(JSON_OPTIONS, token);
            LogLogin_Success(body.Uid);
            _jwtBearerToken = body.Token;
        }, token);

    private async Task<object?> _tryRequestRetryingOnUnauthorizedAsync<TResponse, TRequest>(
        string method, string url, TRequest? reqBody, CancellationToken token)
    {
        await _lockedTask(_taskifyAction(() =>
        {
            if (_jwtBearerToken is not null)
                    _client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _jwtBearerToken);
        }), token);
        LogDoUrlTry1_Begin(method, url, _jwtBearerToken is not null);

        var result = await PerformRequest();
        object? responseBody = new Unused();
        
        switch ((int)result.StatusCode)
        {
            case >= 200 and <= 299:
                await DecodeResponseBody();
                return responseBody;
            case (int)HttpStatusCode.Unauthorized:
                LogDoUrlTry1_FailedAuth();
                await RefreshJwtBearerTokenAsync(token);
                break;
            default: // TODO: handle 1xx, 3xx
                LogDoUrlTry1_FailedOther(method, url, result.StatusCode);
                throw new HttpRequestException($"could not {method} {url}", null, result.StatusCode);
        }
        
        await _lockedTask(_taskifyAction(() =>
        {
            if (_jwtBearerToken is null)
                throw new InvalidOperationException("unexpected: the bearer token is null");
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _jwtBearerToken);
        }), token);
        LogDoUrlTry2_Begin(method, url);
        result = await PerformRequest();
        if (!result.IsSuccessStatusCode)
        {
            LogDoUrlTry2_Failed(method, url, result.StatusCode);
            throw new HttpRequestException($"could not {method} {url}", null, result.StatusCode);
        }

        await DecodeResponseBody();
        return responseBody;

        Task<HttpResponseMessage> PerformRequest()
            => method switch
            {
                "GET" => _client.GetAsync(url, token),
                "POST.json" => _client.PostAsJsonAsync(url, reqBody, JSON_OPTIONS, token),
                "PUT.json" => _client.PutAsJsonAsync(url, reqBody, JSON_OPTIONS, token),
                "POST.empty" => _client.PostAsync(url, new StringContent(""), token),
                _ => throw new ArgumentOutOfRangeException(nameof(method), method, "unhandled method")
            };

        async Task DecodeResponseBody()
        {
            if (typeof(TResponse) == typeof(Unused))
                return;
            responseBody = await result.Content.ReadFromJsonAsync<TResponse>(JSON_OPTIONS, token);
        }
    }

    public async Task<TResponse?> GetAsync<TResponse>(string url, CancellationToken token)
        => (TResponse?)await _tryRequestRetryingOnUnauthorizedAsync<TResponse, Unused>("GET", url, new Unused(), token);
    
    public async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken token)
        => (TResponse?)await _tryRequestRetryingOnUnauthorizedAsync<TResponse, TRequest>("POST.json", url, body, token);

    public async Task<TResponse?> PutJsonAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken token)
        => (TResponse?)await _tryRequestRetryingOnUnauthorizedAsync<TResponse, TRequest>("PUT.json", url, body, token);
    
    public async Task PostJsonNoResponseAsync<TRequest>(string url, TRequest body, CancellationToken token)
        => await _tryRequestRetryingOnUnauthorizedAsync<Unused, TRequest>("POST.json", url, body, token);
    
    public async Task PutJsonNoResponseAsync<TRequest>(string url, TRequest body, CancellationToken token)
        => await _tryRequestRetryingOnUnauthorizedAsync<Unused, TRequest>("PUT.json", url, body, token);

    private readonly record struct Unused;

    [LoggerMessage(LogLevel.Information, "Trying to login user {user}")]
    partial void LogLogin_BeginForUser(string user);

    [LoggerMessage(LogLevel.Information, "login success: uid={uid}")]
    partial void LogLogin_Success(Guid uid);

    [LoggerMessage(LogLevel.Error, "failed to login user {email}")]
    partial void LogLogin_FailedToLoginUserByEmail(string email);

    [LoggerMessage(LogLevel.Information, "{method} {url} (try 1/2) [has_jwt={hasJwt}]")]
    partial void LogDoUrlTry1_Begin(string method, string url, bool hasJwt);

    [LoggerMessage(LogLevel.Information, "failed (1/2) (401)")]
    partial void LogDoUrlTry1_FailedAuth();

    [LoggerMessage(LogLevel.Error, "{method} {url} failed due to non-401 {rc}")]
    partial void LogDoUrlTry1_FailedOther(string method, string url, HttpStatusCode rc);

    [LoggerMessage(LogLevel.Information, "{method} {url} (try 2/2)")]
    partial void LogDoUrlTry2_Begin(string method, string url);

    [LoggerMessage(LogLevel.Error, "{method} {url} failed: {statusCode}")]
    partial void LogDoUrlTry2_Failed(string method, string url, HttpStatusCode statusCode);
}
