using System.Net;
using KotlinScopeFunctions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.Post;
using Request = CsSsg.Src.User.Request;

using CsSsg.Test.Db;

using CsSsg.Test.HtmlApi.Fixture;
using CsSsg.Test.HtmlApi.Html;
using CsSsg.Test.HtmlApi.Http;

namespace CsSsg.Test.HtmlApi.Post;

public class ApiTests : IClassFixture<PostgresFixture>
{
#region scaffolding
    private readonly ILogger<ApiTests> _logger;
    private readonly HttpClient _client;
    
    // this must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;
    private static int _postCounter;

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
    private static int _nextPostId =>  Interlocked.Increment(ref _postCounter);

    private record struct LoggedInUser(Request Details, string SessionCookie);
    
    private async Task<LoggedInUser> _nextSignedUpUserAsync(CancellationToken token)
    {
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] =  user.Email,
                ["password"] =  user.Password,
            }, token: token);
        var sessionCookie = response.TryGetSessionCookie();
        Assert.False(string.IsNullOrEmpty(sessionCookie));
        return new LoggedInUser(user, sessionCookie);
    }
    
    private Request _nextDetails()
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        return new Request(Email: $"{nextUserId}@test!html!post", Password: $"test{nextUserId}");
    }
#endregion
#region Create and view post
    [Fact]
    public async Task TestCreatePost_RequiresAuth()
    {
        var response = await _client.GetAsync("/blog/-new");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new HeaderDictionary
            {
                ["Cookie"] = session
            }, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }
    
    [Fact]
    public async Task TestSignup_ThenPreviewCreatePost()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var newTitle = $"Hello {_nextPostId}";
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=previewButton".AsFormSubmitSelector(),
            new HeaderDictionary
            {
                ["Cookie"] = session
            }, new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# World"
            });
        response.EnsureSuccessStatusCode();
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        
        _logger.LogInformation("Check editor fields");
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//h1[contains(.,'Editing: New:')]"));
        var titleField = doc.DocumentNode.SelectSingleNode("//input[@name='title']")
            ?.Attributes["value"]?.Value?.Trim();
        var contentsField = doc.DocumentNode.SelectSingleNode("//textarea[@name='contents']")
            ?.InnerText?.Trim();
        Assert.NotNull(titleField);
        Assert.NotNull(contentsField);
        Assert.Equal(newTitle, titleField);
        Assert.Equal("# World", contentsField);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new HeaderDictionary
            {
                ["Cookie"] = session
            }, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenCheckListing()
    {
        var (user, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var title = $"Hello _{_nextPostId}";
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = title,
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var blogUrl = "/blog";
        Assert.NotNull(fetchUrl);
        response = await _client.GetWithHeadersAsync(blogUrl, sessionHeaders);
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
        Assert.NotNull(node);
        Assert.NotNull(node.SelectSingleNode($"//h3[.='{title}']"));
        Assert.NotNull(node.SelectSingleNode($"//div[contains(., 'Author: {user.Email}')]"));
        Assert.Null(node.SelectSingleNode("//div[contains(., 'Public: Yes')]"));
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        response = await _client.GetWithHeadersAsync(fetchUrl, sessionHeaders);
        response.EnsureSuccessStatusCode();
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.Equal("World", html.DocumentNode.SelectSingleNode("//article//h1")?.InnerText);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenViewIt_FailsForPublic()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        response = await _client.GetAsync(fetchUrl);
        // recall that ContentAccessPermissionsFilter short circuits with 404 if permissions are invalid
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endregion
#region Update post
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdatePostWithoutAuth_Fails()
    {
        // we start from an empty slate so need to create a post to have a slug to call update on
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new HeaderDictionary
            {
                ["Cookie"] = session
            }, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString?.Split('/')?.Last();
        Assert.NotNull(slug);
        
        _logger.LogInformation("Attempt to publicly fetch update page");
        response = await _client.GetAsync($"/blog/{slug}/edit");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdatePost_RequiresAntiforgery()
    {
        // we start from an empty slate so need to create a post to have a slug to call update on
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var cookieHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            cookieHeaders,
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString.SlugName();
        Assert.NotNull(slug);
        
        _logger.LogInformation("Attempt to publicly commit update without csrf");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            cookieHeaders,
            new Dictionary<string, string>
            {
                ["title"] = $"Goodye {_nextPostId}",
                ["contents"] = "# Universe"
            }, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenPreviewUpdate()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var cookieHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };

        _logger.LogInformation("Create post");
        var postFields = new Dictionary<string, string>
        {
            ["title"] = $"Hello {_nextPostId}",
            ["contents"] = "# World"
        };
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            cookieHeaders, postFields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString?.Split('/')?.Last();
        Assert.NotNull(slug);

        _logger.LogInformation("fetch update page to query edit fields");
        response = await _client.GetWithHeadersAsync($"/blog/{slug}/edit", cookieHeaders);
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var titleField = doc.DocumentNode.SelectSingleNode("//input[@name='title']")
            ?.Attributes["value"]?.Value?.Trim();
        var contentsField = doc.DocumentNode.SelectSingleNode("//textarea[@name='contents']")
            ?.InnerText?.Trim();
        Assert.NotNull(titleField);
        Assert.NotNull(contentsField);
        Assert.Equal(postFields["title"], titleField);
        Assert.Equal(postFields["contents"], contentsField);
        
        _logger.LogInformation("update");
        var newTitle = $"Goodbye {_nextPostId}";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=previewButton".AsFormSubmitSelector(),
            cookieHeaders,
            new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# Universe"
            });
        
        response.EnsureSuccessStatusCode();
        doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.NotNull(doc.DocumentNode.SelectSingleNode($"//h1[contains(.,'Editing: {newTitle}')]"));
       
        _logger.LogInformation("Check editor fields");
        titleField = doc.DocumentNode.SelectSingleNode("//input[@name='title']")
            ?.Attributes["value"]?.Value?.Trim();
        contentsField = doc.DocumentNode.SelectSingleNode("//textarea[@name='contents']")
            ?.InnerText?.Trim();
        Assert.NotNull(titleField);
        Assert.NotNull(contentsField);
        Assert.Equal(newTitle, titleField);
        Assert.Equal("# Universe", contentsField);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var cookieHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };

        _logger.LogInformation("Create post");
        var postFields = new Dictionary<string, string>
        {
            ["title"] = $"Hello {_nextPostId}",
            ["contents"] = "# World"
        };
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            cookieHeaders, postFields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString?.Split('/')?.Last();
        Assert.NotNull(slug);

        _logger.LogInformation("fetch update page to query edit fields");
        response = await _client.GetWithHeadersAsync($"/blog/{slug}/edit", cookieHeaders);
        var doc = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var titleField = doc.DocumentNode.SelectSingleNode("//input[@name='title']")
            ?.Attributes["value"]?.Value?.Trim();
        var contentsField = doc.DocumentNode.SelectSingleNode("//textarea[@name='contents']")
            ?.InnerText?.Trim();
        Assert.NotNull(titleField);
        Assert.NotNull(contentsField);
        Assert.Equal(postFields["title"], titleField);
        Assert.Equal(postFields["contents"], contentsField);
        
        _logger.LogInformation("update");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            cookieHeaders,
            new Dictionary<string, string>
            {
                ["title"] = $"Goodye {_nextPostId}",
                ["contents"] = "# Universe"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenCheckListing()
    {
        var (user, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var title = $"Hello _{_nextPostId}";
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = title,
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        var blogUrl = "/blog";
        Assert.NotNull(slug);
        
        _logger.LogInformation("update");
        var newTitle = $"Goodbye {_nextPostId}";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# Universe"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        
        response = await _client.GetWithHeadersAsync(blogUrl, sessionHeaders);
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
        Assert.NotNull(node);
        Assert.NotNull(node.SelectSingleNode($"//h3[.='{newTitle}']"));
        Assert.NotNull(node.SelectSingleNode($"//div[contains(., 'Author: {user.Email}')]"));
        Assert.Null(node.SelectSingleNode("//div[contains(., 'Public: Yes')]"));
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);
        
        _logger.LogInformation("update");
        var newTitle = $"Goodbye {_nextPostId}";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# Universe"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        
        response = await _client.GetWithHeadersAsync(fetchUrl!, sessionHeaders);
        response.EnsureSuccessStatusCode();
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.Equal("Universe", html.DocumentNode.SelectSingleNode("//article//h1")?.InnerText);
    }
#endregion
#region Rename post tests
    [Fact]
    public async Task TestCreatePost_ThenRenameIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["newname"] = newSlug
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        fetchUrl = response.Headers.Location?.OriginalString;
        slug = fetchUrl?.SlugName();
        Assert.Equal(Contents.ComputeSlugName(newSlug), slug);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_RequiresAuth()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);
        
        _logger.LogInformation("Attempt to rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }
    
    public async Task TestCreatePost_ThenRename_ThenFetchIt_FailsForOldName()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["newname"] = newSlug
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        response = await _client.GetAsync($"/blog/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            sessionHeaders, new Dictionary<string, string>
            {
                ["newname"] = newSlug
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        fetchUrl = response.Headers.Location?.OriginalString;
        slug = fetchUrl?.SlugName();
        Assert.Equal(Contents.ComputeSlugName(newSlug), slug);
        _logger.LogInformation("Fetch entry");
        response = await _client.GetWithHeadersAsync($"/blog/{slug}", sessionHeaders);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.Equal("World", html.DocumentNode.SelectSingleNode("//article//h1")?.InnerText);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_FailsForPublic()
    {
        var (_, session1) = await _nextSignedUpUserAsync(CancellationToken.None);
        var session1Headers = new HeaderDictionary
        {
            ["Cookie"] = session1
        };
        var (_, session2) = await _nextSignedUpUserAsync(CancellationToken.None);
        var session2Headers = new HeaderDictionary
        {
            ["Cookie"] = session2
        };
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            session1Headers, new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);
            
        _logger.LogInformation("Attempt to rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            session2Headers, new Dictionary<string, string>
            {
                ["newname"] = newSlug
            });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endregion
}

internal static class PostSupport
{
    extension(string? s)
    {
        public string? SlugName()
        {
            if (s == null) return null;
            var components = s.Split('/');
            Assert.Equal(3, components.Length);
            Assert.Equal("blog", components[1]);
            return components[2];
        }
    }
}