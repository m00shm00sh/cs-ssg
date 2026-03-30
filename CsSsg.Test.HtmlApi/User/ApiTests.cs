using System.Net;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.User;
using CsSsg.Test.Db;

using CsSsg.Test.HtmlApi.Fixture;
using CsSsg.Test.HtmlApi.Html;

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
#region Signup and login
    [Fact]
    public async Task TestUserSignup()
    {
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var doc = new HtmlDocument();
        var response = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentsStr = await response.Content.ReadAsStringAsync();
        doc.LoadHtml(contentsStr);
        var form = doc.DocumentNode.SelectNodes("//form").First();
        var httpAntiforgery = response.Headers.GetValues("set-cookie")
            .First(s => s.Contains(".AspNetCore.Antiforgery")).Split(';')[0];
        var formInputs  = form.SelectNodes(".//input");
        var formAntiforgery = formInputs.First(node =>
            node.MatchesAttributes(("type", "hidden"), ("name", "__RequestVerificationToken")));
        Assert.NotNull(formInputs.FirstOrDefault(node => 
            node.MatchesAttributes(("type", "text"), ("name", "email"))));
        Assert.NotNull(formInputs.FirstOrDefault(node => 
            node.MatchesAttributes(("type", "password"), ("name", "password"))));
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
        response = await _client.PostAsync(signupUrl,
            new FormUrlEncodedContent(signupForm).WithHeaders(signupHeaders));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
#endregion
}