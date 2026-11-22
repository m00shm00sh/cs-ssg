using System.Net;
using KotlinScopeFunctions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

using Request = CsSsg.Src.User.Request;

using CsSsg.Test.Db;

using CsSsg.Test.HtmlApi.Fixture;
using CsSsg.Test.HtmlApi.Html;
using CsSsg.Test.HtmlApi.Http;
using static CsSsg.Test.HtmlApi.Http.RequestUtils;

namespace CsSsg.Test.HtmlApi.User;

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
        var user = new Request(Email: $"{nextUserId}@test!user", Password: $"test{nextUserId}");
        return user;
    }
#endregion
#region Signup, login, signout
[Fact]
    public async Task TestUserSignup()
    {
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.NotNull(response.TryGetSessionCookie());
    }
    
    [Fact]
    public async Task TestUserSignup_DoesNotSetCookieOnFailure()
    {
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  "/",
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.TryGetSessionCookie());
    }
    
    [Fact]
    public async Task TestUserSignup_RequiresAntiforgery()
    {
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, skipCsrf: true, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
    
    [Fact]
    public async Task TestUserSignup_ThenLogin()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        
        _logger.LogInformation("Do login");
        response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=loginButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        var sessionCookie = response.TryGetSessionCookie();
        Assert.NotNull(sessionCookie);
    }
    
    [Fact]
    public async Task TestLogin_DoesNotSetCookieOnFailure()
    {
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=loginButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  "/",
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.TryGetSessionCookie());
    }
    
    [Fact]
    public async Task TestUserLogin_RequiresAntiforgery()
    {
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=loginButton".AsFormSubmitSelector(),
            RequestUtils.EMPTY_FORM,
            skipCsrf: true, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
    
    [Fact]
    public async Task TestUserSignup_ThenSignout()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.TryGetSessionCookie();
        Assert.NotNull(sessionCookie);
        
        _logger.LogInformation("Sign out");
        response = await _client.PostCookieAsync("/auth/signout", sessionCookie, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // signout logs us out if it sets the .aspnetcore.cookies value to nothing
        var signoutCookie = response.TryGetSessionCookie();
        Assert.NotNull(signoutCookie);
        var signoutValue = signoutCookie.Split(';')[0].Split('=')[1];
        Assert.True(string.IsNullOrEmpty(signoutValue));
    }
#endregion
#region User home
    [Fact]
    public async Task TestUserHome_RequiresAuth()
    {
        var response = await _client.GetAsync("/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestUserSignup_ThenHome()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.TryGetSessionCookie();
        Assert.NotNull(sessionCookie);
        _logger.LogInformation("Get user home");
        response = await _client.GetWithOptionsAsync("/user", new GetOptions { Cookie = sessionCookie}, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
#endregion
#region Sign out (navigation)
    [Fact]
    public async Task TestUserSignup_ThenHome_ThenSignout()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.TryGetSessionCookie();
        Assert.NotNull(sessionCookie);
        _logger.LogInformation("Get user home");
        response = await _client.PostProtectedFormAsync("/user", "value=Sign out".AsFormSubmitSelector(),
            RequestUtils.EMPTY_FORM, sessionCookie, skipCsrf: true, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
#endregion
#region Update details
    [Fact]
    public async Task TestUserDetailsPage_RequiresAuth()
    {
        var response = await _client.GetAsync("/user/details", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestUserSignup_ThenHome_ThenDetails()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.TryGetSessionCookie();
        Assert.NotNull(sessionCookie);
        response = await _client.GetWithOptionsAsync("/user", new GetOptions { Cookie = sessionCookie}, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        // we verified endpoint behavior in TestUserSignup_ThenDetails so now just verify
        // the form points to the correct one
        // we do not reuse PostProtectedFormAsync because this is a GET-GET not a GET[fetch csrf]-POST[submit csrf]
        var updateEndpoint = doc.DocumentNode.SelectSingleNode("//form//input[@value='Update details']/..")
            ?.Let(n => new
            {
                Method = n.Attributes["method"]?.Value,
                Url = n.Attributes["action"]?.Value,
            });
        Assert.Equal("/user/details", updateEndpoint?.Url);
        Assert.Equal("GET", updateEndpoint?.Method?.ToUpper());
    }
    
    [Fact]
    public async Task TestUserSignup_ThenDetails()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.TryGetSessionCookie();
        Assert.NotNull(sessionCookie);
        response = await _client.GetWithOptionsAsync("/user/details", new GetOptions { Cookie = sessionCookie}, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        
        var updateEndpoint = doc.DocumentNode.SelectSingleNode("//form")
            ?.Let(n => new
            {
                Method = n.Attributes["method"]?.Value,
                Url = n.Attributes["action"]?.Value,
            });
        var oldEmail = doc.DocumentNode.SelectSingleNode("//form//input[@name='old_email']")
            ?.Attributes["value"]?.Value;
        Assert.Equal(user.Email, oldEmail);
        // we do not reuse PostProtectedFormAsync because this is a GET-GET not a GET[fetch csrf]-POST[submit csrf]
        Assert.Equal("/user/details.1", updateEndpoint?.Url);
        Assert.Equal("POST", updateEndpoint?.Method?.ToUpper());
    }
    
    [Fact]
    public async Task TestSetDetails_RequiresAuth()
    {
        var response = await _client.PostEmptyAsync("/user/details.1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestUserSignup_ThenChangeDetails_FailsForInvalidToken()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync("/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] = user.Email,
                ["password"] = user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
            ?.Let(s => s.Split(';')[0]);
        Assert.NotNull(sessionCookie);
        
        _logger.LogInformation("Use update form to fetch first antiforgery set");
        response = await _client.GetWithOptionsAsync("/user/details", new GetOptions { Cookie = sessionCookie}, token: TestContext.Current.CancellationToken);
        var (doc, antiforgery1) = await response.ParseAntiforgeryForm(token: TestContext.Current.CancellationToken);
        
        _logger.LogInformation("Create next user");
        user = _nextDetails();
        response = await _client.PostProtectedFormAsync("/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] = user.Email,
                ["password"] = user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
            ?.Let(s => s.Split(';')[0]);
        Assert.NotNull(sessionCookie);

        _logger.LogInformation("Attempt to use user 1 antiforgery with user 2 session");
        response = await _client.PostProtectedFormAsync(doc, antiforgery1, "name=updateButton".AsFormSubmitSelector(),
            new Dictionary<string, string>(), sessionCookie, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task TestUserSignup_ThenChangeDetails()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync("/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] = user.Email,
                ["password"] = user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
            ?.Let(s => s.Split(';')[0]);
        Assert.NotNull(sessionCookie);
        
        _logger.LogInformation("Update email");
        var u2 = _nextDetails();
        response = await _client.PostProtectedFormAsync("/user/details", "name=updateButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["old_email"] = user.Email,
                ["email"] = u2.Email,
                ["password"] = u2.Password
            }, sessionCookie, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // we verify that the change details actually committed in
        // CsSsg.Test.User.ApiTests.TestUserSignup_ThenLogin_ThenModify_Commits 
    }
#endregion
#region Delete user tests
    [Fact]
    public async Task TestDeleteUser_RequiresAuth()
    {
        var response = await _client.PostEmptyAsync("/user/delete", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenDelete()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync("/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] = user.Email,
                ["password"] = user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
            ?.Let(s => s.Split(';')[0]);
        Assert.NotNull(sessionCookie);

        _logger.LogInformation("Delete user from update page");
        response = await _client.PostProtectedFormAsync("/user/details", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["old_email"] = user.Email,
                ["cb_delete"] = "on"
            }, sessionCookie, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var signoutCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"));
        Assert.NotNull(signoutCookie);
        var signoutValue = signoutCookie.Split(';')[0].Split('=')[1];
        Assert.True(string.IsNullOrEmpty(signoutValue));
    }
    
    [Fact]
    public async Task TestSignup_ThenDelete_RequiresConfirmation()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync("/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] = user.Email,
                ["password"] = user.Password,
            }, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
            ?.Let(s => s.Split(';')[0]);
        Assert.NotNull(sessionCookie);

        _logger.LogInformation("Delete user from update page");
        response = await _client.PostProtectedFormAsync("/user/details", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["old_email"] = user.Email,
            }, sessionCookie, token: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("missing confirmation", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
#endregion
}