using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.Media;
using MObject = CsSsg.Src.Media.Object;
using MC = CsSsg.Src.Post.IManageCommand;
using CsSsg.Src.User;
using Request = CsSsg.Src.User.Request;

using CsSsg.Test.Db;

using CsSsg.Test.JsonApi.Fixture;
using CsSsg.Test.JsonApi.Http;
using CsSsg.Test.StreamSupport;

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
    
    private static int _nextUserId =>  Interlocked.Increment(ref _userCounter);
    private static int _nextFileId =>  Interlocked.Increment(ref _fileCounter);

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
        var _ = await response.ReadAsJsonAsync<string>();
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
        response = await _client.ApiGetWithBearerAsync("/media", token);
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        var entry = entries
            .First(e => e.Slug == slugName
                    && e.ContentType == file.ContentType
                    && e.Size == stream.Length
                    && !e.IsPublic);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenViewIt()
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
        response = await _client.ApiGetWithBearerAsync($"/media/{slugName}", token);
        response.EnsureSuccessStatusCode();
        
        var cType = response.Content.Headers.ContentType?.ToString();
        var bodyResponse = await response.Content.ReadAsByteArrayAsync();
        stream.Seekable = true;
        stream.Seek(0, SeekOrigin.Begin);
        var expResponse = await stream.SaveToArrayAsync();
        Assert.Equal(cType, file.ContentType);
        Assert.Equal(expResponse, bodyResponse);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreateMedia_ThenViewIt_FailsForPublic()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.a";
        var response = await _client.ApiPostFileWithBearerAsync("/media", token, name, file);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Attempt to fetch post");
        response = await _client.ApiGetAsync($"/media/{slugName}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
        response = await _client.ApiGetWithBearerAsync("/media", token);
        response.EnsureSuccessStatusCode();
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        var _ = entries
            .First(e => e.Slug == slugName
                && e.ContentType == file.ContentType
                && e.Size == stream2.Length
                && !e.IsPublic);
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
        response = await _client.ApiGetWithBearerAsync($"/media/{slugName}", token);
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
        response = await _client.ApiGetWithBearerAsync($"/media/{slugName}/stats", token);
        response.EnsureSuccessStatusCode();
        var stats = await response.ReadAsJsonAsync<Stats>();
        stream.Seekable = true;
        Assert.Equal("a/a", stats.ContentType);
        Assert.Equal(stream.Length, stats.Size);
        Assert.Equal(new MC.Permissions(), stats.Permissions);
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
        response = await _client.ApiGetAsync($"/media/{slugName}/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        response = await _client.ApiGetWithBearerAsync($"/media/{slugName}", token);
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
        response = await _client.ApiGetWithBearerAsync($"/media/{newSlug}", token);
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
#region Change media permissions tests
    [Fact]
    public async Task TestCreateMedia_ThenMakeItPublic()
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
        var cmd = new MC.SetPermissions(new MC.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenMakeItPublic_RequiresAuth()
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
        var cmd = new MC.SetPermissions(new MC.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonAsync($"/media/{slugName}/permissions", cmd);
       Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItPublic_ThenViewItPublicly()
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
        var cmd = new MC.SetPermissions(new MC.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("View post publicly");
        response = await _client.ApiGetAsync($"/media/{slugName}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestCreateMedia_ThenMakeItPublic_ThenMakeItPrivateAgain()
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
        var cmd = new MC.SetPermissions(new MC.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Change perms back");
        cmd = new MC.SetPermissions(new MC.Permissions());
        response = await _client.ApiPostJsonWithBearerAsync($"/media/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("Attempt to view post publicly");
        response = await _client.ApiGetAsync($"/media/{slugName}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
        response = await _client.ApiGetWithBearerAsync($"/media/{slugName}", token2);
        response.EnsureSuccessStatusCode();
        
        _logger.LogInformation("Attempt to fetch post with old uid");
        response = await _client.ApiGetWithBearerAsync($"/media/{slugName}", token1);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
        response = await _client.ApiGetWithBearerAsync($"/media/{slugName}", token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endregion
}