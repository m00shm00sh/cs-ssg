using System.Net;
using HtmlAgilityPack;
using KotlinScopeFunctions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.User;
using CsSsg.Test.Db;

using CsSsg.Test.HtmlApi.Fixture;
using CsSsg.Test.HtmlApi.Html;
using CsSsg.Test.HtmlApi.Http;

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
            AllowAutoRedirect = false
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
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _logger.LogInformation("Parse signin/signup form");
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        doc.LoadHtml(await response.Content.ReadAsStringAsync());
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var httpAntiforgery = response.Headers.GetValues("set-cookie")
            .First(s => s.Contains(".AspNetCore.Antiforgery")).Split(';')[0];
        var formInputs  = form.SelectNodes("//input");
        var formAntiforgery = formInputs.First(node =>
            node.MatchesAttributes(("type", "hidden"), ("name", "__RequestVerificationToken")));
        Assert.NotNull(formInputs.FirstOrDefault(node => 
            node.MatchesAttributes(("type", "text"), ("name", "email"))));
        Assert.NotNull(formInputs.FirstOrDefault(node => 
            node.MatchesAttributes(("type", "password"), ("name", "password"))));
        _logger.LogInformation("Prepare signup action");
        var signupForm = new Dictionary<string, string>
        {
            ["email"] =  user.Email,
            ["password"] =  user.Password,
            [formAntiforgery.Attributes["name"].Value] = formAntiforgery.Attributes["Value"].Value
        };
        var signupHeaders = new HeaderDictionary
        {
            ["Cookie"] = httpAntiforgery
        };
        var signupUrl = formInputs.First(node =>
            node.MatchesAttributes(("type", "submit"), ("name", "signupButton")))
            .Attributes["formaction"].Value;
        _logger.LogInformation("Do signup");
        response = await _client.PostFormAsync(signupUrl, signupHeaders, signupForm);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // signup logs us in if it sets the .aspnetcore.cookies (we do not check for keys)
        Assert.NotNull(response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies")));
    }
    
    [Fact]
    public async Task TestUserSignup_RequiresAntiforgery()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _logger.LogInformation("Parse signin/signup form");
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var formInputs  = form.SelectNodes("//input");
        _logger.LogInformation("Prepare signup action");
        var signupForm = new Dictionary<string, string>
        {
            ["email"] =  user.Email,
            ["password"] =  user.Password,
        };
        var signupUrl = formInputs.First(node =>
                node.MatchesAttributes(("type", "submit"), ("name", "signupButton")))
            .Attributes["formaction"].Value;
        _logger.LogInformation("Attempt signup");
        response = await _client.PostFormAsync(signupUrl, signupForm);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }
    
    [Fact]
    public async Task TestUserSignup_ThenLogin()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _logger.LogInformation("Parse signin/signup form");
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var httpAntiforgery = response.Headers.GetValues("set-cookie")
            .First(s => s.Contains(".AspNetCore.Antiforgery")).Split(';')[0];
        var formInputs  = form.SelectNodes("//input");
        var formAntiforgery = formInputs.First(node =>
            node.MatchesAttributes(("type", "hidden"), ("name", "__RequestVerificationToken")));
        _logger.LogInformation("Prepare signup action");
        var signupForm = new Dictionary<string, string>
        {
            ["email"] =  user.Email,
            ["password"] =  user.Password,
            [formAntiforgery.Attributes["name"].Value] = formAntiforgery.Attributes["Value"].Value
        };
        var signupHeaders = new HeaderDictionary
        {
            ["Cookie"] = httpAntiforgery
        };
        var signupUrl = formInputs.First(node =>
            node.MatchesAttributes(("type", "submit"), ("name", "signupButton")))
            .Attributes["formaction"].Value;
        _logger.LogInformation("Do signup");
        response = await _client.PostFormAsync(signupUrl, signupHeaders, signupForm);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        _logger.LogInformation("Prepare login");
        var loginUrl = formInputs.First(node =>
                node.MatchesAttributes(("type", "submit"), ("name", "loginButton")))
            .Attributes["formaction"].Value;
        _logger.LogInformation("Do login");
        response = await _client.PostFormAsync(loginUrl, signupHeaders, signupForm);
        Assert.NotNull(response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies")));
    }
    
    [Fact]
    public async Task TestUserLogin_RequiresAntiforgery()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _logger.LogInformation("Parse signin/signup form");
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var formInputs  = form.SelectNodes("//input");
        var signupForm = new Dictionary<string, string>
        {
            ["email"] =  user.Email,
            ["password"] =  user.Password,
        };
        _logger.LogInformation("Attempt login");
        var loginUrl = formInputs.First(node =>
                node.MatchesAttributes(("type", "submit"), ("name", "loginButton")))
            .Attributes["formaction"].Value;
        response = await _client.PostFormAsync(loginUrl, signupForm);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }
    
    [Fact]
    public async Task TestUserSignup_ThenSignout()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _logger.LogInformation("Parse signin/signup form");
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var httpAntiforgery = response.Headers.GetValues("set-cookie")
            .First(s => s.Contains(".AspNetCore.Antiforgery")).Split(';')[0];
        var formInputs  = form.SelectNodes("//input");
        var formAntiforgery = formInputs.First(node =>
            node.MatchesAttributes(("type", "hidden"), ("name", "__RequestVerificationToken")));
        Assert.NotNull(formInputs.FirstOrDefault(node => 
            node.MatchesAttributes(("type", "text"), ("name", "email"))));
        Assert.NotNull(formInputs.FirstOrDefault(node => 
            node.MatchesAttributes(("type", "password"), ("name", "password"))));
        _logger.LogInformation("Prepare signup action");
        var signupForm = new Dictionary<string, string>
        {
            ["email"] =  user.Email,
            ["password"] =  user.Password,
            [formAntiforgery.Attributes["name"].Value] = formAntiforgery.Attributes["Value"].Value
        };
        var signupHeaders = new HeaderDictionary
        {
            ["Cookie"] = httpAntiforgery
        };
        var signupUrl = formInputs.First(node =>
            node.MatchesAttributes(("type", "submit"), ("name", "signupButton")))
            .Attributes["formaction"].Value;
        _logger.LogInformation("Do signup");
        response = await _client.PostFormAsync(signupUrl, signupHeaders, signupForm);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // signup logs us in if it sets the .aspnetcore.cookies (we do not check for keys)
        var sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"));
        Assert.NotNull(sessionCookie);
        var signoutHeaders = new HeaderDictionary
        {
            [".AspNetCore.Cookies"] = sessionCookie
        };
        response = await _client.PostHeadersAsync("/auth/signout", signoutHeaders);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // signout logs us out if it sets the .aspnetcore.cookies value to nothing
        var signoutCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"));
        Assert.NotNull(signoutCookie);
        var signoutValue = signoutCookie.Split(';')[0].Split('=')[1];
        Assert.True(string.IsNullOrEmpty(signoutValue));
    }
#endregion
#region User home
    [Fact]
    public async Task TestUserHome_RequiresAuth()
    {
        var response = await _client.GetAsync("/user");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestUserSignup_ThenHome()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentsStr = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("Parse signin/signup form");
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var httpAntiforgery = response.Headers.GetValues("set-cookie")
            .First(s => s.Contains(".AspNetCore.Antiforgery")).Split(';')[0];
        var formInputs = form.SelectNodes("//input");
        var formAntiforgery = formInputs.First(node =>
            node.MatchesAttributes(("type", "hidden"), ("name", "__RequestVerificationToken")));
        _logger.LogInformation("Prepare signup action");
        var signupForm = new Dictionary<string, string>
        {
            ["email"] = user.Email,
            ["password"] = user.Password,
            [formAntiforgery.Attributes["name"].Value] = formAntiforgery.Attributes["Value"].Value
        };
        var signupHeaders = new HeaderDictionary
        {
            ["Cookie"] = httpAntiforgery
        };
        var signupUrl = formInputs
            .First(node => node.MatchesAttributes(("type", "submit"), ("name", "signupButton")))
            .Attributes["formaction"].Value;
        _logger.LogInformation("Do signup");
        response = await _client.PostFormAsync(signupUrl, signupHeaders, signupForm);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
            ?.Let(s => s.Split(';')[0]);
        Assert.NotNull(sessionCookie);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = sessionCookie,
        };
        response = await _client.GetWithHeadersAsync("/user", sessionHeaders);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
#endregion
#region Sign out (navigation)
    [Fact]
    public async Task TestUserSignup_ThenHome_ThenSignout()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _logger.LogInformation("Parse signin/signup form");
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var httpAntiforgery = response.Headers.GetValues("set-cookie")
            .First(s => s.Contains(".AspNetCore.Antiforgery")).Split(';')[0];
        var formInputs = form.SelectNodes("//input");
        var formAntiforgery = formInputs.First(node =>
            node.MatchesAttributes(("type", "hidden"), ("name", "__RequestVerificationToken")));
        _logger.LogInformation("Prepare signup action");
        var signupForm = new Dictionary<string, string>
        {
            ["email"] = user.Email,
            ["password"] = user.Password,
            [formAntiforgery.Attributes["name"].Value] = formAntiforgery.Attributes["Value"].Value
        };
        var signupHeaders = new HeaderDictionary
        {
            ["Cookie"] = httpAntiforgery
        };
        var signupUrl = formInputs.First(node =>
                node.MatchesAttributes(("type", "submit"), ("name", "signupButton")))
            .Attributes["formaction"].Value;
        _logger.LogInformation("Do signup");
        response = await _client.PostFormAsync(signupUrl, signupHeaders, signupForm);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sessionCookie = response.Headers.GetValues("set-cookie")
            .FirstOrDefault(s => s.Contains(".AspNetCore.Cookies"))
            ?.Let(s => s.Split(';')[0]);
        Assert.NotNull(sessionCookie);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = sessionCookie,
        };
        response = await _client.GetWithHeadersAsync("/user", sessionHeaders);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        doc = new HtmlDocument();
        doc.LoadHtml(await response.Content.ReadAsStringAsync());
        // we verified endpoint behavior in TestUserSignup_ThenSignout so now just verify
        // the form points to the correct one
        var signoutEndpoint = doc.DocumentNode.SelectSingleNode("//form//input[@value='Sign out']/..")
            .Let(n => new
            {
                Method = n.Attributes["method"].Value,
                Url = n.Attributes["action"].Value,
            });
        Assert.Equal("/auth/signout", signoutEndpoint.Url);
        Assert.Equal("POST", signoutEndpoint.Method.ToUpper());
    }
#endregion
}