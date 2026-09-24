using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.Media;
using Entry = CsSsg.Src.Media.Entry;
using MObject = CsSsg.Src.Media.Object;
using CsSsg.Src.Post;
using MC = CsSsg.Src.Post.IManageCommand;
using CsSsg.Src.User;
using Request = CsSsg.Src.User.Request;

using CsSsg.Test.Db;
using CsSsg.Test.JsonApi.Fixture;
using CsSsg.Test.JsonApi.Http;
using CsSsg.Test.Post;
using LibApiTests = CsSsg.Test.Post.ApiTests;
using CsSsg.Test.SharedTypes;
using CsSsg.Test.StreamSupport;

using static CsSsg.Test.JsonApi.Http.RequestUtils;

namespace CsSsg.Test.JsonApi.Media;

public class ApiTests : IClassFixture<PostgresFixture>
{
    #region scaffolding

    private readonly ILogger<ApiTests> _logger;
    private readonly HttpClient _client;

    // this must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;
    private static int _fileCounter;

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
    private static int _nextFileId => Interlocked.Increment(ref _fileCounter);

    private record struct LoggedInUser(Request Details, string Bearer);

    private async Task<LoggedInUser> _nextSignedUpUserAsync(CancellationToken token)
    {
        var user = _nextDetails();
        var response = await _client.ApiPostJsonAsync("/auth/signup", user, token: token);
        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsJsonAsync<LoginResponse>(token);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        return new LoggedInUser(user, body.Token);
    }

    private Request _nextDetails()
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        return new Request(Email: $"{nextUserId}@test!json!media", Password: $"test{nextUserId}");
    }

    #endregion

    #region Create and view media

    [Fact]
    public async Task TestCreateMedia_RequiresAuth()
    {
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileAsync("/media", name, file);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        await response.ReadAsJsonAsync<string>();
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_RequiresContentType()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("content-type", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_RequiresFilename()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, "", file);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("content-disposition", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenCheckListing()
    {
        var (user, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        stream.Seekable = true;

        _logger.LogInformation("Fetch listing");
        response = await _client.ApiGetWithOptionsAsync("/media", new GetOptions { Bearer = token });
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        _ = entries.First(e =>
            e.Slug == slugName
            && e.ContentType == file.ContentType
            && e.Size == stream.Length
            && e.RevisionCount == 1
            && !e.IsUnlisted());
    }

    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [Theory]
    public async Task TestSignup_ThenCreateMedia_ThenViewIt(bool publicFetch, HttpStatusCode expStatus)
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}", new GetOptions
        {
            Bearer = !publicFetch ? token : null
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

    [InlineData(false, HttpStatusCode.NotModified)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [Theory]
    public async Task TestSignup_ThenCreateMedia_ThenViewIt_SkipsConditionally(bool publicRefetch,
        HttpStatusCode expStatus)
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        var fetchUrl = $"/media/{slugName}";

        _logger.LogInformation("Fetch media");
        response = await _client.ApiGetWithOptionsAsync(fetchUrl, new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var lastModified = response.Content.Headers.LastModified;

        _logger.LogInformation("Fetch entry conditionally");
        response = await _client.ApiGetWithOptionsAsync(fetchUrl, new GetOptions
        {
            Bearer = !publicRefetch ? token : null,
            IfModifiedSince = lastModified
        });
        Assert.Equal(expStatus, response.StatusCode);
    }

    #endregion

    #region Update post

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdatePostWithoutAuth_Fails()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Attempt to publicly update");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.ApiPutFileAsync($"/media/{slugName}", file);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Update media");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.ApiPutFileWithBearerAsync($"/media/{slugName}", token, file);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt_RequiresContentType()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Update media");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("", stream2);
        response = await _client.ApiPutFileWithBearerAsync($"/media/{slugName}", token, file);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("content-type", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt_ThenCheckListing()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Update media");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.ApiPutFileWithBearerAsync($"/media/{slugName}", token, file);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        stream2.Seekable = true;

        _logger.LogInformation("Check listing");
        response = await _client.ApiGetWithOptionsAsync("/media", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        _ = entries.First(e =>
            e.Slug == slugName
            && e.ContentType == file.ContentType
            && e.Size == stream2.Length
            && e.RevisionCount == 2
            && !e.IsUnlisted());
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt_ThenViewIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Update media");
        await using var stream2 = new RepeatingByteStream(2, 2);
        file = new MObject("a/a", stream2);
        response = await _client.ApiPutFileWithBearerAsync($"/media/{slugName}", token, file);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();

        var cType = response.Content.Headers.ContentType?.ToString();
        var bodyResponse = await response.Content.ReadAsByteArrayAsync();
        stream2.Seekable = true;
        stream2.Seek(0, SeekOrigin.Begin);
        var expResponse = await stream2.SaveToArrayAsync();
        Assert.Equal(cType, file.ContentType);
        Assert.Equal(expResponse, bodyResponse);
    }

    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenUpdateIt_ThenViewRevisions()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        var nameRef = RefBox.Create("");

        var objs = await AsyncEnumerable.Range(1, 2).Select(async (r, _, _) =>
        {
            switch (r)
            {
                case 1:
                    _logger.LogInformation("Create media");
                    var stream = new RepeatingByteStream(1, 1);
                    var file = new MObject("a/a", stream);
                    var name = $"smiley{_nextFileId}.a";
                    var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
                    response.EnsureSuccessStatusCode();
                    var slugName = await response.ReadAsJsonAsync<string>();
                    nameRef.Value = slugName!;
                    stream.Seekable = true;
                    stream.Seek(0, SeekOrigin.Begin);
                    return file;
                case 2:
                    _logger.LogInformation("Update media");
                    var slug = nameRef.AssertedValue(string.IsNullOrEmpty, invert: true);
                    var stream2 = new RepeatingByteStream(2, 2);
                    var file2 = new MObject("a/a", stream2);
                    response = await _client.ApiPutFileWithBearerAsync($"/media/{slug}", token, file2);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                    return file2;
                default:
                    throw new InvalidOperationException($"unexpected case {r}");
            }
        }).ToListAsync();

        var tab = new[]
        {
            new { Revision = 1, ExpStatus = HttpStatusCode.OK },
            new { Revision = 3, ExpStatus = HttpStatusCode.NotFound }
        };
        await Assert.AllAsync(tab, async arg =>
        {
            _logger.LogInformation("Fetch revision {}", arg.Revision);
            var name = nameRef.AssertedValue(string.IsNullOrEmpty, invert: true);
            var response = await _client.ApiGetWithOptionsAsync($"/media/{name}?revision={arg.Revision}",
                new GetOptions { Bearer = token });

            switch (arg.ExpStatus)
            {
                case HttpStatusCode.OK:
                    response.EnsureSuccessStatusCode();
                    var cType = response.Content.Headers.ContentType?.ToString();
                    var bodyResponse = await response.Content.ReadAsByteArrayAsync();
                    var obj = objs[arg.Revision - 1];
                    var stream = obj.ContentStream;
                    var expResponse = await stream.SaveToArrayAsync();
                    Assert.Equal(cType, obj.ContentType);
                    Assert.Equal(expResponse, bodyResponse);
                    await stream.DisposeAsync();
                    break;
                default:
                    Assert.Equal(arg.ExpStatus, response.StatusCode);
                    return;
            }
        });
    }

    #endregion

    #region Media stats tests

    [Fact]
    public async Task TestCreatePost_ThenGetItsStats()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch stats");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}/stats", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var stats = await response.ReadAsJsonAsync<Stats>();
        stream.Seekable = true;
        Assert.Equal("a/a", stats.ContentType);
        Assert.Equal(stream.Length, stats.Size);
        Assert.Equal(new MC.PostTags(), stats.Tags, PostTagsEqualityComparer.Instance);
    }

    [Fact]
    public async Task TestCreatePost_ThenGetItsStats_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Attempt to fetch stats");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}/stats");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted_ThenViewItsStatsUnlistedly()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new MC.SetTags(new MC.PostTags
        {
            Visibility = MC.PostVisibility.Unlisted
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("View stats publicly");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    #endregion

    #region Rename media tests

    [Fact]
    public async Task TestCreateMedia_ThenRenameIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Rename entry");
        var newSlug = $"smiley{_nextFileId}.b";
        var cmd = new MC.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenRenameIt_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Attempt to rename entry");
        var newSlug = $"smiley{_nextFileId}.b";
        var cmd = new MC.Rename(newSlug);
        response = await _client.ApiPostJsonAsync($"/media/{slugName}/rename", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenRename_ThenFetchIt_FailsForOldName()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Rename entry");
        var newSlug = $"smiley{_nextFileId}.b";
        var cmd = new MC.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Attempt to fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}", new GetOptions { Bearer = token });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenRenameIt_ThenViewIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Rename entry");
        var newSlug = $"smiley{_nextFileId}.b";
        var cmd = new MC.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Fetch media");
        response = await _client.ApiGetWithOptionsAsync($"/media/{newSlug}", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var cType = response.Content.Headers.ContentType?.ToString();
        var bodyResponse = await response.Content.ReadAsByteArrayAsync();
        stream.Seekable = true;
        stream.Seek(0, SeekOrigin.Begin);
        var expResponse = await stream.SaveToArrayAsync();
        Assert.Equal(file.ContentType, cType);
        Assert.Equal(expResponse, bodyResponse);
    }

    #endregion

    #region Change media tags tests

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new MC.SetTags(new MC.PostTags
        {
            Visibility = MC.PostVisibility.Unlisted
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new MC.SetTags(new MC.PostTags
        {
            Visibility = MC.PostVisibility.Unlisted
        });
        response = await _client.ApiPostJsonAsync($"/media/{slugName}/tags", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted_ThenViewItUnlistedly()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new MC.SetTags(new MC.PostTags
        {
            Visibility = MC.PostVisibility.Unlisted
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("View post publicly");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItUnlisted_ThenMakeItPrivateAgain()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new MC.SetTags(new MC.PostTags
        {
            Visibility = MC.PostVisibility.Unlisted
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Change perms back");
        cmd = new MC.SetTags(new MC.PostTags());
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Attempt to view post publicly");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenSetTags_ThenFilterByExtraTags()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        ICollection<string> auxTags = ["X"];

        _logger.LogInformation("Create posts and apply permissions");
        var entries = await AsyncEnumerable.Range(0, 2).Select(async (i, _, _) =>
        {
            _logger.LogInformation("Create media");
            await using var stream = new RepeatingByteStream(1, 1);
            var file = new MObject("a/a", stream);
            var name = $"smiley{_nextFileId}.a";
            var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
            response.EnsureSuccessStatusCode();
            var slugName = await response.ReadAsJsonAsync<string>();

            if (i % 2 == 1)
            {
                _logger.LogInformation("Change tags");
                var cmd = new IManageCommand.SetTags(new IManageCommand.PostTags { Tags = auxTags });
                response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/tags", token, cmd);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            return slugName;
        }).ToListAsync(CancellationToken.None);

        _logger.LogInformation("Fetch listing");
        var utcNow = DateTime.UtcNow;
        var response = await _client.ApiGetWithOptionsAsync("/media", new GetOptions { Bearer = token },
            auxTags.Select(t => ("xtags", t)));
        response.EnsureSuccessStatusCode();
        var gotEntries = (await response.ReadAsJsonAsync<List<Entry>>())!
            .Select(e => e.Slug)
            .ToList();
        Assert.Contains(entries[1], gotEntries);
        Assert.DoesNotContain(entries[0], gotEntries);
    }

    #endregion

    #region Change media author tests

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Change author");
        var cmd = new MC.SetAuthor(u2.Email);
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/chauthor", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Attempt to change author");
        var cmd = new MC.SetAuthor(u2.Email);
        response = await _client.ApiPostJsonAsync($"/media/{slugName}/chauthor", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_FailsForInvalidNewAuthor()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Attempt to change author");
        var cmd = new MC.SetAuthor("@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@");
        response = await _client.ApiPostJsonAsync($"/media/{slugName}/chauthor", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_TransfersOwnership()
    {
        var (_, token1) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token1, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change author");
        var (u2, token2) = await _nextSignedUpUserAsync(CancellationToken.None);
        var cmd = new MC.SetAuthor(u2.Email);
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/chauthor", token1, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}", new GetOptions { Bearer = token2 });
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Attempt to fetch post with old uid");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}", new GetOptions { Bearer = token1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Delete media tests

    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Delete media");
        response = await _client.ApiDeleteWithBearerAsync($"/media/{slugName}", token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Attempt to delete media");
        response = await _client.ApiDeleteAsync($"/media/{slugName}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt_DeletesIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Delete media");
        response = await _client.ApiDeleteWithBearerAsync($"/media/{slugName}", token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Attempt to fetch");
        response = await _client.ApiGetWithOptionsAsync($"/media/{slugName}", new GetOptions { Bearer = token });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion
    
    #region Mixed revision types

    [MemberData(nameof(Test.Post.ApiTests.RevisionSequencePermutations), MemberType = typeof(Test.Post.ApiTests))]
    [Theory]
    public async Task TestCreatePost_ThenPerformMixedOperationsToGetPolymorphicRevisionHistory(
        IList<LibApiTests.RevisionType> revisionSequence)
    {
        var baseContext = new Post.ApiTests.RevisionMakerContextForWebApitest(_logger, _client);
        LibApiTests.RevisionType[] seq = [default, ..revisionSequence];
        var token = CancellationToken.None;

        await LibApiTests.PolymorphicRevisionHistoryWorker(baseContext, CreateNextUser, MakePostRevision, seq,
            FetchPostRevisionMetadata, null, token);
        return;

        async Task<(string, LibApiTests.IRevisionMakerUserSession)> CreateNextUser(LibApiTests.IRevisionMakerContext ctx,
            CancellationToken _)
        {
            var (email, bearer) = await _nextSignedUpUserAsync(token);
            var userSession = new Post.ApiTests.RevisionMakerJsonApitestUserContext(bearer);
            return (email.Email, userSession);
        }

        static async Task<IRevision> MakePostRevision(LibApiTests.RevisionMakerSession sess,
            LibApiTests.RevisionType revT, int revIdx, CancellationToken token)
        {
            var (logger, client) = (Post.ApiTests.RevisionMakerContextForWebApitest)sess.Context;
            var uSess = (Post.ApiTests.RevisionMakerJsonApitestUserContext)sess.UserSession;
            var bearer = uSess.Bearer;
            var userEmail = sess.UserEmail;
            var slugRef = sess.SlugRef;

            if (revIdx == 0)
            {
                logger.LogInformation("Create media");
                await using var stream = new RepeatingByteStream(1, 1);
                var file = new MObject("a/a", stream);
                var name = $"smiley{_nextFileId}.a";
                var response = await client.ApiPostFileWithBearerAsync("/media", bearer, name, file);
                response.EnsureSuccessStatusCode();
                var slugName = await response.ReadAsJsonAsync<string>();
                slugRef.Value = slugName!;
                return new Src.Media.Revision
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
                    logger.LogInformation("Update media");
                    await using var stream2 = new RepeatingByteStream(2, 2);
                    var file = new MObject("a/a", stream2);
                    var response = await client.ApiPutFileWithBearerAsync($"/media/{slug}", bearer, file);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                    return new Src.Media.Revision
                    {
                        AuthorHandle = userEmail,
                        Number = revIdx + 1
                    };
                }
                case LibApiTests.RevisionType.Tag:
                {
                    logger.LogInformation("Change tags");
                    var cmd = new MC.SetTags(new MC.PostTags
                    {
                        Tags = [$"r{revIdx}"]
                    });
                    var response = await client.ApiPostJsonWithBearerAsync($"/media/{slug}/tags", bearer, cmd);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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
            var (logger, client) = (Post.ApiTests.RevisionMakerContextForWebApitest)sess.Context;
            var uSess = (Post.ApiTests.RevisionMakerJsonApitestUserContext)sess.UserSession;
            var bearer = uSess.Bearer;
            var slug = sess.SlugRef.Value;

            logger.LogInformation("Fetch stats");
            var response = await client.ApiGetWithOptionsAsync($"/media/{slug}/stats", 
                new GetOptions { Bearer = bearer });
            response.EnsureSuccessStatusCode();
            var stats = await response.ReadAsJsonAsync<Stats>();
            return stats.Revisions;
        }
    }

    #endregion
}