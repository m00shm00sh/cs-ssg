using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.Post;
using Request = CsSsg.Src.User.Request;

using CsSsg.Test.Db;
using LibApiTests = CsSsg.Test.Post.ApiTests;
using CsSsg.Test.SharedTypes;

using CsSsg.Test.HtmlApi.Fixture;
using CsSsg.Test.HtmlApi.Html;
using static CsSsg.Test.HtmlApi.Html.Matchers;
using CsSsg.Test.HtmlApi.Http;
using static CsSsg.Test.HtmlApi.Http.RequestUtils;

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

    private static int _nextUserId => Interlocked.Increment(ref _userCounter);
    private static int _nextPostId => Interlocked.Increment(ref _postCounter);

    private record struct LoggedInUser(Request Details, string SessionCookie);

    private async Task<LoggedInUser> _nextSignedUpUserAsync(CancellationToken token)
    {
        var user = _nextDetails();
        var response = await _client.PostProtectedFormAsync(
            "/auth/login", "name=signupButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["email"] = user.Email,
                ["password"] = user.Password,
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
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session, skipCsrf: true);
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
            new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# World"
            }, session);
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
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenCheckListing()
    {
        var (user, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var title = $"Hello _{_nextPostId}";
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = title,
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var blogUrl = "/blog";
        Assert.NotNull(fetchUrl);
        response = await _client.GetWithOptionsAsync(blogUrl, new GetOptions { Cookie = session });
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
        Assert.NotNull(node);
        Assert.NotNull(node.SelectSingleNode($"//h3[.='{title}']"));
        Assert.NotNull(node.SelectSingleNode($"//div[contains(., 'Author: {user.Email}')]"));
        Assert.NotNull(node.SelectSingleNode("//div[contains(., 'Revision count: 1')]"));
        Assert.Null(node.SelectSingleNode("//div[contains(., 'Public: Yes')]"));
    }

    [Fact]
    public async Task TestSignup_ThenCreatePosts_ThenCheckListingForUser()
    {
        var (user1, session1) = await _nextSignedUpUserAsync(CancellationToken.None);
        var (user2, session2) = await _nextSignedUpUserAsync(CancellationToken.None);

        var entries = await AsyncEnumerable.Range(0, 4).Select(async (i, _, _) =>
        {
            var doPublic = (i & 1) != 0;
            var whichCookie = (i & 2) == 0 ? session1 : session2;
            _logger.LogInformation("post {}: create", i);
            var title = $"Hello _{_nextPostId}";
            var response = await _client.PostProtectedFormAsync("/blog/-new",
                "name=submitButton".AsFormSubmitSelector(),
                new Dictionary<string, string>
                {
                    ["title"] = title,
                    ["contents"] = "# World"
                }, whichCookie);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var fetchUrl = response.Headers.Location?.OriginalString;
            Assert.NotNull(fetchUrl);
            var slugName = fetchUrl.SlugName()!;

            _logger.LogInformation("post {}: chperm", i);
            response = await _client.PostProtectedFormAsync(
                $"/blog/{slugName}/manage", "value=Change tags".AsFormSubmitSelector(),
                new Dictionary<string, string>
                {
                    ["visibility"] = doPublic ? "public" : ""
                }, whichCookie);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return slugName;
        }).ToListAsync();

        var blogUrl = "/blog";

        var tab = new[]
        {
            new { name = "listing_u1_uo", cookie = session1, qUser = user1.Email, expIndices = new[] { 0, 1 } },
            new { name = "listing_u1_u2o", cookie = session1, qUser = user2.Email, expIndices = new[] { 3 } }
        };

        await Assert.AllAsync(tab, async arg =>
        {
            var got = await FetchSlugs(arg.cookie, arg.qUser);
            var exp = entries.SelectIndices(arg.expIndices);
            Assert.Equal(exp.Order(), got.Order());
        });
        return;

        [SuppressMessage("ReSharper", "VariableHidesOuterVariable")]
        async Task<IEnumerable<string>> FetchSlugs(string? cookie, string? qUser)
        {
            var uri = blogUrl;
            if (qUser is not null)
                uri += "?user=" + WebUtility.UrlEncode(qUser);
            var response = await _client.GetWithOptionsAsync(uri, new GetOptions { Cookie = cookie });
            var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
            var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
            var got = listing
                ?.SelectNodes("//li/section/a/@href")
                ?.Select(e => e.Attributes["href"].Value)
                ?.Select(s => s.SlugName()!);
            return got == null ? [] : got.Where(entries.Contains);
        }
    }

    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [Theory]
    public async Task TestSignup_ThenCreatePost_ThenViewIt(bool publicFetch, HttpStatusCode expStatus)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);

        _logger.LogInformation("Fetch");
        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions
        {
            Cookie = !publicFetch ? session : null
        });
        if ((int)expStatus is >= 200 and <= 299)
        {
            response.EnsureSuccessStatusCode();
            var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
            Assert.Equal("World", html.DocumentNode.SelectSingleNode("//article//h1")?.InnerText);
        }
        else
            Assert.Equal(expStatus, response.StatusCode);
    }

    [InlineData(false, HttpStatusCode.NotModified)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [Theory]
    public async Task TestSignup_ThenCreatePost_ThenViewIt_SkipsConditionally(bool publicRefetch,
        HttpStatusCode expStatus)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        _logger.LogInformation("Fetch entry");
        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions { Cookie = session });
        response.EnsureSuccessStatusCode();
        var lastModified = response.Content.Headers.LastModified;

        _logger.LogInformation("Fetch entry conditionally");
        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions
        {
            Cookie = !publicRefetch ? session : null,
            IfModifiedSince = lastModified
        });
        Assert.Equal(expStatus, response.StatusCode);
    }

    // this verifies that the name slug regex is working adequately
    [Fact]
    public async Task TestSignup_ThenCreateDuplicatePost_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);

        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions { Cookie = session });
        response.EnsureSuccessStatusCode();
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
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
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
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Attempt to publicly commit update without csrf");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Goodye {_nextPostId}",
                ["contents"] = "# Universe"
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenPreviewUpdate()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var postFields = new Dictionary<string, string>
        {
            ["title"] = $"Hello {_nextPostId}",
            ["contents"] = "# World"
        };
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            postFields, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString?.Split('/')?.Last();
        Assert.NotNull(slug);

        _logger.LogInformation("fetch update page to query edit fields");
        response = await _client.GetWithOptionsAsync($"/blog/{slug}/edit", new GetOptions { Cookie = session });
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
            new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# Universe"
            }, session);

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

        _logger.LogInformation("Create post");
        var postFields = new Dictionary<string, string>
        {
            ["title"] = $"Hello {_nextPostId}",
            ["contents"] = "# World"
        };
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            postFields, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString?.Split('/')?.Last();
        Assert.NotNull(slug);

        _logger.LogInformation("fetch update page to query edit fields");
        response = await _client.GetWithOptionsAsync($"/blog/{slug}/edit", new GetOptions { Cookie = session });
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
            new Dictionary<string, string>
            {
                ["title"] = $"Goodye {_nextPostId}",
                ["contents"] = "# Universe"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenCheckListing()
    {
        var (user, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var title = $"Hello _{_nextPostId}";
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = title,
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        var blogUrl = "/blog";
        Assert.NotNull(slug);

        _logger.LogInformation("update");
        var newTitle = $"Goodbye {_nextPostId}";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# Universe"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        response = await _client.GetWithOptionsAsync(blogUrl, new GetOptions { Cookie = session });
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
        Assert.NotNull(node);
        Assert.NotNull(node.SelectSingleNode($"//h3[.='{newTitle}']"));
        Assert.NotNull(node.SelectSingleNode($"//div[contains(., 'Author: {user.Email}')]"));
        Assert.NotNull(node.SelectSingleNode("//div[contains(., 'Revision count: 2')]"));
        Assert.Null(node.SelectSingleNode("//div[contains(., 'Public: Yes')]"));
    }

    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync("/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("update");
        var newTitle = $"Goodbye {_nextPostId}";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = newTitle,
                ["contents"] = "# Universe"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);


        response = await _client.GetWithOptionsAsync(fetchUrl!, new GetOptions { Cookie = session });
        response.EnsureSuccessStatusCode();
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.Equal("Universe", html.DocumentNode.SelectSingleNode("//article//h1")?.InnerText);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenViewRevisions()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        var slugRef = RefBox.Create("");

        var revMatchers = await AsyncEnumerable.Range(1, 2).Select(async (r, _, _) =>
        {
            switch (r)
            {
                case 1:
                    _logger.LogInformation("Create post");
                    var response = await _client.PostProtectedFormAsync("/blog/-new",
                        "name=submitButton".AsFormSubmitSelector(),
                        new Dictionary<string, string>
                        {
                            ["title"] = $"Hello {_nextPostId}",
                            ["contents"] = "# World"
                        }, session);
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    var fetchUrl = response.Headers.Location?.OriginalString;
                    var slug = fetchUrl?.SlugName();
                    Assert.NotNull(slug);
                    slugRef.Value = slug;
                    return DocumentMatcher(doc =>
                        Assert.Equal("World", doc.DocumentNode.SelectSingleNode("//article//h1")?.InnerText));
                case 2:
                    _logger.LogInformation("update");
                    slug = slugRef.AssertedValue(string.IsNullOrEmpty, invert: true);
                    var newTitle = $"Goodbye {_nextPostId}";
                    response = await _client.PostProtectedFormAsync(
                        $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
                        new Dictionary<string, string>
                        {
                            ["title"] = newTitle,
                            ["contents"] = "# Universe"
                        }, session);
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    return DocumentMatcher(doc =>
                        Assert.Equal("Universe", doc.DocumentNode.SelectSingleNode("//article//h1")?.InnerText));
                default:
                    throw new InvalidOperationException($"unexpected case {r}");
            }
        }).ToListAsync();

        var tab = new[]
        {
            new { RevisionNumber = 1, ExpStatusCode = HttpStatusCode.OK },
            new { RevisionNumber = 3, ExpStatusCode = HttpStatusCode.NotFound }
        };

        await Assert.AllAsync(tab, async arg =>
        {
            var url = $"/blog/{slugRef.AssertedValue(string.IsNullOrEmpty, invert: true)}?revision={arg.RevisionNumber}";
            var response = await _client.GetWithOptionsAsync(url, new GetOptions { Cookie = session });
            switch (arg.ExpStatusCode)
            {
                case HttpStatusCode.OK:
                    response.EnsureSuccessStatusCode();
                    var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
                    revMatchers[arg.RevisionNumber - 1](html);
                    return;
                default:
                    Assert.Equal(arg.ExpStatusCode, response.StatusCode);
                    break;
            }
        });
    }

    #endregion

    #region Manage page tests

    [Fact]
    public async Task TestCreatePost_ThenAccessManagePage_FailsForPublic()
    {
        var (_, session1) = await _nextSignedUpUserAsync(CancellationToken.None);

        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session1);
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

    [InlineData(IManageCommand.PostVisibility.Public)]
    [InlineData(IManageCommand.PostVisibility.Unlisted)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility_ThenViewItsManagePagePublicly(
        IManageCommand.PostVisibility newVisibility)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = newVisibility.ToString().ToLower()
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Fetch manage page publicly");
        response = await _client.GetAsync($"/blog/{slug}/manage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.Null(html.DocumentNode.SelectSingleNode("//div[contains(@class, 'manage-actions-container')]"));
    }
    
    #endregion

    #region Rename post tests

    [Fact]
    public async Task TestCreatePost_ThenRenameIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        fetchUrl = response.Headers.Location?.OriginalString;
        slug = fetchUrl?.SlugName();
        Assert.Equal(Contents.ComputeSlugName(newSlug), slug);
    }

    [Fact]
    public async Task TestCreatePost_ThenRenameIt_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreatePost_ThenRename_ThenFetchIt_FailsForOldName()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        response = await _client.GetAsync($"/blog/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenRenameIt_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        fetchUrl = response.Headers.Location?.OriginalString;
        slug = fetchUrl?.SlugName();
        Assert.Equal(Contents.ComputeSlugName(newSlug), slug);
        _logger.LogInformation("Fetch entry");
        response = await _client.GetWithOptionsAsync($"/blog/{slug}", new GetOptions { Cookie = session });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.Equal("World", html.DocumentNode.SelectSingleNode("//article//h1")?.InnerText);
    }

    #endregion

    #region Change post tags tests

    [InlineData(IManageCommand.PostVisibility.Public)]
    [InlineData(IManageCommand.PostVisibility.Unlisted)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility(IManageCommand.PostVisibility newVisibility)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = newVisibility.ToString().ToLower()
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenChangeItsTags_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>(), session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [InlineData(IManageCommand.PostVisibility.Public)]
    [InlineData(IManageCommand.PostVisibility.Unlisted)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility_ThenViewItPublicly(
        IManageCommand.PostVisibility newVisibility)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = newVisibility.ToString().ToLower()
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Fetch entry publicly");
        response = await _client.GetAsync($"/blog/{slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        Assert.Equal("World", html.DocumentNode.SelectSingleNode("//article//h1")?.InnerText);
    }

    [InlineData(IManageCommand.PostVisibility.Public, true)]
    [InlineData(IManageCommand.PostVisibility.Unlisted, false)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility_ThenCheckListing(
        IManageCommand.PostVisibility newVisibility, bool shouldExistInListing)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var title = $"Hello {_nextPostId}";
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = title,
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = newVisibility.ToString().ToLower()
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var blogUrl = "/blog";
        response = await _client.GetAsync(blogUrl);
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        if (shouldExistInListing)
        {
            var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
            Assert.NotNull(node);
            Assert.NotNull(node.SelectSingleNode($"//h3[.='{title}']"));
        }
        else
        {
            var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
            Assert.Null(node);
        }
    }

    [InlineData(IManageCommand.PostVisibility.Public)]
    [InlineData(IManageCommand.PostVisibility.Unlisted)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility_ThenMakeItPrivateAgain(
        IManageCommand.PostVisibility newVisibility)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = newVisibility.ToString().ToLower()
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Change entry permissions back");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>(), session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Attempt to fetch entry publicly");
        response = await _client.GetAsync($"/blog/{slug}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePosts_ThenSetTags_ThenFilterByExtraTags()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        ICollection<string> auxTags = ["X"];

        _logger.LogInformation("Create posts and apply permissions");
        var entries = await AsyncEnumerable.Range(0, 2).Select(async (i, _, _) =>
        {
            _logger.LogInformation("Create post");
            var title = $"Hello {_nextPostId}";
            var response = await _client.PostProtectedFormAsync(
                "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
                new Dictionary<string, string>
                {
                    ["title"] = title,
                    ["contents"] = "# World"
                }, session);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var fetchUrl = response.Headers.Location?.OriginalString;
            var slug = fetchUrl?.SlugName();
            Assert.NotNull(slug);

            if (i % 2 == 1)
            {
                _logger.LogInformation("Change entry permissions");
                response = await _client.PostProtectedFormAsync(
                    $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
                    new Dictionary<string, string>
                    {
                        ["tags"] = string.Join(" ", auxTags)
                    }, session);
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            }

            return new { Title = title, Slug = slug };
        }).ToListAsync(CancellationToken.None);

        var blogUrl = "/blog";
        var response = await _client.GetWithOptionsAsync(blogUrl, new GetOptions { Cookie = session },
            auxTags.Select(s => ("xtags", s)));
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");

        Assert.NotNull(listing.SelectSingleNode($"//h3[.='{entries[1].Title}']"));
        Assert.Null(listing.SelectSingleNode($"//h3[.='{entries[0].Title}']"));
    }

    #endregion

    #region Change post author tests

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Sign up next user");
        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Change author");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = u2.Email
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Sign up next user");
        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Attempt to change author");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = u2.Email
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_FailsForInvalidNewAuthor()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Attempt to change author");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"
            }, session);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_TransfersOwnership()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Sign up next user");
        var (u2, session2) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Attempt to change author");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = u2.Email
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        response = await _client.GetWithOptionsAsync($"/blog/{slug}", new GetOptions { Cookie = session });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        response = await _client.GetWithOptionsAsync($"/blog/{slug}", new GetOptions { Cookie = session2 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Delete post tests

    [Fact]
    public async Task TestCreatePost_ThenDeleteIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Delete post");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["cb_delete"] = "on"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenDeleteIt_RequiresConfirmation()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        var sessionHeaders = new HeaderDictionary
        {
            ["Cookie"] = session
        };
        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Delete");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>(), session);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("delete confirmation", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreatePost_ThenDeleteIt_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Attempt to delete");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["cb_delete"] = "on"
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreatePost_ThenDeleteIt_DeletesIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var response = await _client.PostProtectedFormAsync(
            "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["title"] = $"Hello {_nextPostId}",
                ["contents"] = "# World"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        var slug = fetchUrl?.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Delete post");
        response = await _client.PostProtectedFormAsync(
            $"/blog/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["cb_delete"] = "on"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Attempt to fetch");
        response = await _client.GetWithOptionsAsync($"/blog/{slug}", new GetOptions { Cookie = session });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion
    
    #region Mixed revision types

    // internal because shared by Media/ApiTest
    internal readonly record struct RevisionMakerContextForWebApitest(ILogger Logger, HttpClient Client)
        : LibApiTests.IRevisionMakerContext;

    // internal because shared by Media/ApiTest
    internal readonly record struct RevisionMakerHtmlApitestUserContext(string Cookie)
        : LibApiTests.IRevisionMakerUserSession;

    [MemberData(nameof(LibApiTests.RevisionSequencePermutations), MemberType = typeof(LibApiTests))]
    [Theory]
    public async Task TestCreatePost_ThenPerformMixedOperationsToGetPolymorphicRevisionHistory(
        IList<LibApiTests.RevisionType> revisionSequence)
    {
        var baseContext = new RevisionMakerContextForWebApitest(_logger, _client);
        LibApiTests.RevisionType[] seq = [default, ..revisionSequence];
        var token = CancellationToken.None;

        await LibApiTests.PolymorphicRevisionHistoryWorker(baseContext, CreateNextUser, MakePostRevision, seq,
            FetchPostRevisionMetadata, null, token);
        return;

        async Task<(string, LibApiTests.IRevisionMakerUserSession)> CreateNextUser(LibApiTests.IRevisionMakerContext ctx,
            CancellationToken _)
        {
            var (email, cookie) = await _nextSignedUpUserAsync(token);
            var userSession = new RevisionMakerHtmlApitestUserContext(cookie);
            return (email.Email, userSession);
        }

        static async Task<IRevision> MakePostRevision(LibApiTests.RevisionMakerSession sess,
            LibApiTests.RevisionType revT, int revIdx, CancellationToken token)
        {
            var (logger, client) = (RevisionMakerContextForWebApitest)sess.Context;
            var uSess = (RevisionMakerHtmlApitestUserContext)sess.UserSession;
            var cookie = uSess.Cookie;
            var userEmail = sess.UserEmail;
            var slugRef = sess.SlugRef;

            if (revIdx == 0)
            {
                logger.LogInformation("Create post");
                var post = new Contents($"Hello {_nextPostId}", "# World");
                var response = await client.PostProtectedFormAsync(
                    "/blog/-new", "name=submitButton".AsFormSubmitSelector(),
                    new Dictionary<string, string>
                    {
                        ["title"] = $"Hello {_nextPostId}",
                        ["contents"] = "# World"
                    }, cookie, token: token);
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                var fetchUrl = response.Headers.Location?.OriginalString;
                var slugName = fetchUrl?.SlugName();
                Assert.NotNull(slugName);
                slugRef.Value = slugName;
                
                return new Revision
                {
                    AuthorHandle = userEmail,
                    Number = 1
                };
            }

            var slug = slugRef.AssertedValue(string.IsNullOrEmpty, invert: true);
            switch (revT)
            {
                case LibApiTests.RevisionType.Content:
                {
                    logger.LogInformation("Update");
                    
                    var response = await client.PostProtectedFormAsync(
                        $"/blog/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
                        new Dictionary<string, string>
                        {
                            ["title"] = $"Goodye {_nextPostId}",
                            ["contents"] = "# World World"
                        }, cookie, token: token);
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    
                    return new Revision
                    {
                        AuthorHandle = userEmail,
                        Number = revIdx + 1
                    };
                }
                case LibApiTests.RevisionType.Tag:
                {
                    logger.LogInformation("Change tags");
                    var response = await client.PostProtectedFormAsync(
                        $"/blog/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
                        new Dictionary<string, string>
                        {
                            ["tags"] = string.Join(" ", $"X{revIdx+1}")
                        }, cookie, token: token);
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    
                    return new TagRevision
                    {
                        AuthorHandle = userEmail,
                        Number = revIdx + 1
                    };
                }
            }

            throw new ArgumentOutOfRangeException(nameof(revT), revT, "unhandled case");
        }

        static async Task<IEnumerable<IRevision>> FetchPostRevisionMetadata(LibApiTests.RevisionMakerSession sess,
            CancellationToken token)
        {
            var (logger, client) = (RevisionMakerContextForWebApitest)sess.Context;
            var uSess = (RevisionMakerHtmlApitestUserContext)sess.UserSession;
            var cookie = uSess.Cookie;
            var slug = sess.SlugRef.Value;

            logger.LogInformation("Fetch stats");
            var response = await client.GetWithOptionsAsync($"/blog/{slug}/manage", 
                new GetOptions { Cookie = cookie }, token: token);
            var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync(token));
            
            var revNodes = html.DocumentNode.SelectNodes("//tr[contains(@class, 'revision-row')]");

            var revisions = revNodes
                .Select(IRevision (node) =>
                {
                    var revNumNode = node.SelectSingleNode(".//td[contains(@class, 'revision-number')]")
                                     ?? throw new InvalidOperationException(
                                         "unexpected: no match for td.revision-number");
                    var revComNode = node.SelectSingleNode(".//td[contains(@class, 'revision-common')]")
                                     ?? throw new InvalidOperationException(
                                         "unexpected: no match for td.revision-common");
                    var entryNode = node.SelectSingleNode(".//div[contains(@class, 'revision-entry')]")
                                    ?? throw new InvalidOperationException(
                                        "unexpected: no match for div.revision-entry");
                    var classes = entryNode.Attributes["class"]?.Value?.Split(' ') ?? [];
                    var revNum = int.Parse(revNumNode.InnerText);
                    var comText = revComNode.InnerText;
                    var author = Regex.Match(comText, @"Author: (.*)\s*$").Groups[1].Value;
                    var isTagEntry = classes.Contains("tag-revision");
                    var isPostEntry = classes.Contains("post-revision");
                    if (!(isTagEntry ^ isPostEntry))
                        throw new InvalidOperationException("invalid: both or neither of: post tag");
                    if (isTagEntry)
                    {
                        // we don't save the deleted and added values when creating the expected revision so don't
                        // bother listifying and just do count to verify there's at least one element in the tag delta
                        var nDeleted = entryNode.SelectNodes(".//div[contains(@class, 'deleted-tags-container')]//span")
                            ?.Select(dn => dn.InnerText)
                            .Count() ?? 0;
                        var nAdded = entryNode.SelectNodes(".//div[contains(@class, 'added-tags-container')]//span")
                            ?.Select(dn => dn.InnerText)
                            .Count() ?? 0;
                        if (nDeleted + nAdded == 0)
                            throw new InvalidOperationException("unexpected: no deleted or added tag nodes detected");
                        return new TagRevision
                        {
                            AuthorHandle = author,
                            Number = revNum,
                        };
                    }

                    // likewise here we don't check for content-length
                    return new Revision
                    {
                        AuthorHandle = author,
                        Number = revNum,
                    };
                }).ToList();

            return revisions;
        }
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