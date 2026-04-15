using System.Net;
using CsSsg.Src.User;
using KotlinScopeFunctions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using Request = CsSsg.Src.User.Request;

using CsSsg.Test.Db;

using CsSsg.Test.JsonApi.Fixture;
using CsSsg.Test.JsonApi.Http;

namespace CsSsg.Test.JsonApi.User;

public class ApiTests : IClassFixture<PostgresFixture>
{
#region scaffolding
    private readonly ILogger<ApiTests> _logger;
    private readonly HttpClient _client;
    
    // this must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;

    public ApiTests(PostgresFixture fixture, ITestOutputHelper outputHelper)
    {
        var factory = new WebAppFactory(outputHelper, fixture);
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions()
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        // the logger for the test function itself, not to be confused with the logger configured for asp.net up above
        _logger = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper)).CreateLogger<ApiTests>();
    }
    
    private static int _nextUserId =>  Interlocked.Increment(ref _userCounter);
    
    private Request _nextDetails()
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        var user = new Request(Email: $"{nextUserId}@test!json!user", Password: $"test{nextUserId}");
        return user;
    }
#endregion
#region Signup, login, signout
    [Fact]
    public async Task TestUserSignup()
    {
        var user = _nextDetails();
        var response = await _client.ApiPostJsonAsync("/auth/signup", user);
        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
    }
    
    [Fact]
    public async Task TestUserSignup_PropagatesFailure()
    {
        var user = _nextDetails();
        user = user with { Email = "/" };
        var response = await _client.ApiPostJsonAsync("/auth/signup", user);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    
    [Fact]
    public async Task TestUserSignup_ThenLogin()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.ApiPostJsonAsync("/auth/signup", user);
        response.EnsureSuccessStatusCode();
        response = await _client.ApiPostJsonAsync("/auth/login", user);
        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
    }
    
    [Fact]
    public async Task TestUserLogin_PropagatesFailure()
    {
        var user = _nextDetails();
        user = user with { Email = "/" };
        var response = await _client.ApiPostJsonAsync("/auth/login", user);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    
#endregion
#region Delete user tests
    [Fact]
    public async Task TestSignup_ThenDelete_RequiresToken()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.ApiPostJsonAsync("/auth/signup", user);
        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        _logger.LogInformation("Attempt to delete user");
        response = await _client.ApiDeleteAsync($"/user/{user.Email}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestSignup_ThenDelete()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.ApiPostJsonAsync("/auth/signup", user);
        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsJsonAsync<LoginResponse>();
        var token = body.Token;
        Assert.False(string.IsNullOrWhiteSpace(token));
        _logger.LogInformation("Delete user");
        response = await _client.ApiDeleteWithBearerAsync($"/user/{user.Email}", token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
#endregion
}