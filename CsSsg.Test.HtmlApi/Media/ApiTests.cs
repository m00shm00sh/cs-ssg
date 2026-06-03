using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using MObject = CsSsg.Src.Media.Object;
using Request = CsSsg.Src.User.Request;
using CsSsg.Test.Db;
using CsSsg.Test.HtmlApi.Fixture;
using CsSsg.Test.HtmlApi.Html;
using CsSsg.Test.HtmlApi.Http;
using CsSsg.Test.StreamSupport;
using static CsSsg.Test.HtmlApi.Http.RequestUtils;

namespace CsSsg.Test.HtmlApi.Media;

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
    private static int _nextFileId => Interlocked.Increment(ref _postCounter);

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
        return new Request(Email: $"{nextUserId}@test!html!media", Password: $"test{nextUserId}");
    }

    #endregion

    #region Create and view media

    [Fact]
    public async Task TestCreateMedia_RequiresAuth()
    {
        var response = await _client.GetAsync("/media/-new");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName()!;
        // check against incorrect slugification
        Assert.Equal(1, slug.Where(c => c == '.').Length());
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenCheckListing()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName()!;
        var mediaListUrl = "/media";

        response = await _client.GetWithOptionsAsync(mediaListUrl, new GetOptions { Cookie = session });
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
        Assert.NotNull(node);
        Assert.NotNull(node.SelectSingleNode($"//h3[.='{slug}']"));
        Assert.NotNull(node.SelectSingleNode("//div[contains(., 'Content-type: a/a')]"));
        Assert.NotNull(node.SelectSingleNode("//div[contains(., 'Size: 1')]"));
        Assert.Null(node.SelectSingleNode("//div[.='Unlisted: Yes']"));
    }

    [InlineData(false, HttpStatusCode.NotModified)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [Theory]
    public async Task TestSignup_ThenCreateMedia_ThenViewIt_SkipsConditionally(bool publicFetch,
        HttpStatusCode expStatus)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);

        _logger.LogInformation("fetch entry");
        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions { Cookie = session });
        response.EnsureSuccessStatusCode();
        var lastModified = response.Content.Headers.LastModified;
        _logger.LogInformation("fetch entry conditionally");
        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions
        {
            Cookie = !publicFetch ? session : null,
            IfModifiedSince = lastModified
        });
        Assert.Equal(expStatus, response.StatusCode);
    }

    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [Theory]
    public async Task TestSignup_ThenCreateMedia_ThenViewIt(bool publicFetch, HttpStatusCode expStatus)
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);

        _logger.LogInformation("fetch entry");
        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions
        {
            Cookie = !publicFetch ? session : null
        });

        if ((int)expStatus is >= 200 and <= 299)
        {
            response.EnsureSuccessStatusCode();

            var cType = response.Content.Headers.ContentType?.ToString();
            var bodyResponse = await response.Content.ReadAsByteArrayAsync();
            stream.Seekable = true;
            stream.Seek(0, SeekOrigin.Begin);
            var expResponse = await stream.SaveToArrayAsync();
            Assert.Equal(cType, file.ContentType);
            Assert.Equal(expResponse, bodyResponse);
        }
        else
            Assert.Equal(expStatus, response.StatusCode);
    }

    // this verifies that the name slug regex is working adequately
    [Fact]
    public async Task TestSignup_ThenCreateDuplicateMedia_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        stream.Seekable = true;
        stream.Seek(0, SeekOrigin.Begin);
        response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions { Cookie = session });
        response.EnsureSuccessStatusCode();
    }

    #endregion

    #region Update media

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateItWithoutAuth_Fails()
    {
        // we start from an empty slate so need to create a post to have a slug to call update on
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Attempt to publicly fetch update page");
        response = await _client.GetAsync($"/media/{slug}/edit");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt_RequiresAntiforgery()
    {
        // we start from an empty slate so need to create a post to have a slug to call update on
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Attempt to publicly commit update without csrf");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.PostProtectedMultipartFormAsync(
            $"/media/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var slug = response.Headers.Location?.OriginalString.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Update");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.PostProtectedMultipartFormAsync(
            $"/media/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt_ThenCheckListing()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Update");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.PostProtectedMultipartFormAsync(
            $"/media/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var listingUrl = "/media";
        response = await _client.GetWithOptionsAsync(listingUrl, new GetOptions { Cookie = session });
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        var node = listing.SelectSingleNode($"//li/section/a[@href='{fetchUrl}']/..");
        Assert.NotNull(node);
        Assert.NotNull(node.SelectSingleNode("//div[contains(., 'Size: 2')]"));
        Assert.Null(node.SelectSingleNode("//div[.='Unlisted: Yes']"));
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();
        Assert.NotNull(slug);

        _logger.LogInformation("Update");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.PostProtectedMultipartFormAsync(
            $"/media/{slug}/edit", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        response = await _client.GetWithOptionsAsync(fetchUrl, new GetOptions { Cookie = session });
        response.EnsureSuccessStatusCode();

        var cType = response.Content.Headers.ContentType?.ToString();
        var bodyResponse = await response.Content.ReadAsByteArrayAsync();
        stream2.Seekable = true;
        stream2.Seek(0, SeekOrigin.Begin);
        var expResponse = await stream2.SaveToArrayAsync();
        Assert.Equal(cType, file.ContentType);
        Assert.Equal(expResponse, bodyResponse);
    }

    #endregion

    #region Manage page tests

    [Fact]
    public async Task TestCreateMedia_ThenAccessManagePage_FailsForPublic()
    {
        var (_, session1) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session1);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName()!;

        _logger.LogInformation("Attempt to fetch manage page");
        var newSlug = $"<Hello -{_nextFileId}>";
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Rename media tests

    [Fact]
    public async Task TestCreateMedia_ThenRenameIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Rename entry");
        var newSlug = $"smiley{_nextFileId}.a.b";
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        fetchUrl = response.Headers.Location?.OriginalString;
        slug = fetchUrl?.SlugName();
        Assert.True(slug?.EndsWith("-a.b"));
    }

    [Fact]
    public async Task TestCreateMedia_ThenRenameIt_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Rename entry");
        var newSlug = $"smiley{_nextFileId}.a.b";
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreateMedia_ThenRename_ThenFetchIt_FailsForOldName()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Rename entry");
        var newSlug = $"smiley{_nextFileId}.a.b";
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        response = await _client.GetAsync($"/media/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenRenameIt_ThenViewIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Rename entry");
        var newSlug = $"smiley{_nextFileId}.a.b";
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Rename".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newname"] = newSlug
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        fetchUrl = response.Headers.Location?.OriginalString;

        _logger.LogInformation("fetch entry");
        response = await _client.GetWithOptionsAsync(fetchUrl!, new GetOptions { Cookie = session });
        response.EnsureSuccessStatusCode();
    }

    #endregion

    #region Change media tags tests

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = "unlisted"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>(), session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted_ThenViewItPublicly()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = "unlisted"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Fetch entry publicly");
        response = await _client.GetAsync($"/media/{slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted_ThenMakeItPrivateAgain()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Change entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["visibility"] = "unlisted"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Reset entry permissions");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
            new Dictionary<string, string>(), session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Attempt to fetch entry publicly");
        response = await _client.GetAsync($"/media/{slug}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenSetTags_ThenFilterByExtraTags()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        ICollection<string> auxTags = ["X"];

        _logger.LogInformation("Create media and apply permissions");
        var entries = await AsyncEnumerable.Range(0, 2).Select(async (i, _, _) =>
        {
            await using var stream = new RepeatingByteStream(1, 1);
            var file = new MObject("a/a", stream);
            var name = $"smiley{_nextFileId}.a";
            var response = await _client.PostProtectedMultipartFormAsync(
                "/media/-new", "name=submitButton".AsFormSubmitSelector(),
                new Dictionary<string, IMultipartEntry>
                {
                    ["upload"] = new MultipartFile(name, file)
                }, session);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            
            var fetchUrl = response.Headers.Location?.OriginalString;
            var slug = fetchUrl?.SlugName();
            Assert.NotNull(slug);

            if (i % 2 == 1)
            {
                _logger.LogInformation("Change entry permissions");
                response = await _client.PostProtectedFormAsync(
                    $"/media/{slug}/manage", "value=Change tags".AsFormSubmitSelector(),
                    new Dictionary<string, string>
                    {
                        ["tags"] = string.Join(" ", auxTags)
                    }, session);
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            }

            return slug;
        }).ToListAsync(CancellationToken.None);

        var listingUrl = "/media";
        var response = await _client.GetWithOptionsAsync(listingUrl, new GetOptions { Cookie = session},
            auxTags.Select(s => ("xtags", s)));
        var html = Loaders.LoadHtml(await response.Content.ReadAsStringAsync());
        var listing = html.DocumentNode.SelectSingleNode("//article//ul[@id='listing']");
        
        Assert.NotNull(listing.SelectSingleNode($"//h3[.='{entries[1]}']"));
        Assert.Null(listing.SelectSingleNode($"//h3[.='{entries[0]}']"));
    }

    #endregion

    #region Change media author tests

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Sign up next user");
        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Change author");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = u2.Email
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Sign up next user");
        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Attempt to change author");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = u2.Email
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_FailsForInvalidNewAuthor()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Attempt to change author");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"
            }, session);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_TransfersOwnership()
    {
        var (_, session1) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session1);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Sign up next user");
        var (u2, session2) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Change author");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Set new author".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["newauthor"] = u2.Email
            }, session1);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Fetch");
        response = await _client.GetWithOptionsAsync($"/media/{slug}", new GetOptions { Cookie = session1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        response = await _client.GetWithOptionsAsync($"/media/{slug}", new GetOptions { Cookie = session2 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Delete media tests

    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Delete media");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["cb_delete"] = "on"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt_RequiresConfirmation()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Delete");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>(), session);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("delete confirmation", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt_RequiresAntiforgery()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Attempt to delete");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["cb_delete"] = "on"
            }, session, skipCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt_DeletesIt()
    {
        var (_, session) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.PostProtectedMultipartFormAsync(
            "/media/-new", "name=submitButton".AsFormSubmitSelector(),
            new Dictionary<string, IMultipartEntry>
            {
                ["upload"] = new MultipartFile(name, file)
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var fetchUrl = response.Headers.Location?.OriginalString;
        Assert.NotNull(fetchUrl);
        var slug = fetchUrl.SlugName();

        _logger.LogInformation("Delete post");
        response = await _client.PostProtectedFormAsync(
            $"/media/{slug}/manage", "value=Confirm delete".AsFormSubmitSelector(),
            new Dictionary<string, string>
            {
                ["cb_delete"] = "on"
            }, session);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        _logger.LogInformation("Attempt to fetch");
        response = await _client.GetWithOptionsAsync($"/media/{slug}", new GetOptions { Cookie = session });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion
}

internal static class MediaSupport
{
    extension(string? s)
    {
        public string? SlugName()
        {
            if (s == null) return null;
            var components = s.Split('/');
            Assert.Equal(3, components.Length);
            Assert.Equal("media", components[1]);
            return components[2];
        }
    }
}