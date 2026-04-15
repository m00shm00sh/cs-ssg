using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.Post;
using CsSsg.Src.User;
using Request = CsSsg.Src.User.Request;

using CsSsg.Test.Db;

using CsSsg.Test.JsonApi.Fixture;
using CsSsg.Test.JsonApi.Http;

namespace CsSsg.Test.JsonApi.Post;

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

    private record struct LoggedInUser(Request Details, string Bearer);
    
    private async Task<LoggedInUser> _nextSignedUpUserAsync(CancellationToken token)
    {
        var user = _nextDetails();
        var response = await _client.ApiPostJsonAsync("/auth/signup", user);
        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        return new LoggedInUser(user, body.Token);
    }
    
    private Request _nextDetails()
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        return new Request(Email: $"{nextUserId}@test!json!post", Password: $"test{nextUserId}");
    }
#endregion
#region Create and view post
    [Fact]
    public async Task TestCreatePost_RequiresAuth()
    {
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonAsync("/blog/-new", post);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenCheckListing()
    {
        var (user, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch listing");
        response = await _client.ApiGetWithBearerAsync("/blog", token);
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        var entry = entries
            .First(e => e.Slug == slugName
                && e.Title == post.Title
                && e.AuthorHandle == user.Email
                && !e.IsPublic);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenViewIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}", token);
        response.EnsureSuccessStatusCode();
        var contents = await response.ReadAsJsonAsync<Contents>();
        Assert.Equal(post, contents);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenViewIt_FailsForPublic()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetAsync($"/blog/{slugName}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endregion
#region Update post
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdatePostWithoutAuth_Fails()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Attempt to publicly update");
        post = new Contents($"Hello {_nextPostId}", "# Universe");
        response = await _client.ApiPutJsonAsync($"/blog/{slugName}", post);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
 [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Update");
        post = new Contents($"Hello {_nextPostId}", "# Universe");
        response = await _client.ApiPutJsonWithBearerAsync($"/blog/{slugName}", token, post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenCheckListing()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Update");
        post = new Contents($"Hello {_nextPostId}", "# Universe");
        response = await _client.ApiPutJsonWithBearerAsync($"/blog/{slugName}", token, post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Check listing");
        response = await _client.ApiGetWithBearerAsync("/blog", token);
        response.EnsureSuccessStatusCode();
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        var _ = entries
            .First(e => e.Slug == slugName
                        && e.Title == post.Title
                        && !e.IsPublic);
    }
    
    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenViewIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Update");
        post = new Contents($"Hello {_nextPostId}", "# Universe");
        response = await _client.ApiPutJsonWithBearerAsync($"/blog/{slugName}", token, post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}", token);
        response.EnsureSuccessStatusCode();
        var contents = await response.ReadAsJsonAsync<Contents>();
        Assert.Equal(post, contents);
    }
#endregion
#region Post stats tests
    [Fact]
    public async Task TestCreatePost_ThenGetItsStats()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
            
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
            
        _logger.LogInformation("Fetch stats");
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}/stats", token);
        response.EnsureSuccessStatusCode();
        var stats = await response.ReadAsJsonAsync<IManageCommand.Stats>();
        Assert.Equal(post.Title, stats.Title);
        Assert.Equal(post.Body.Length, stats.ContentLength);
        Assert.Equal(new IManageCommand.Permissions(), stats.Permissions);
    }
        
    [Fact]
    public async Task TestCreatePost_ThenGetItsStats_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
            
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
            
        _logger.LogInformation("Attempt to fetch stats");
        response = await _client.ApiGetAsync($"/blog/{slugName}/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
#endregion
#region Rename post tests
    [Fact]
    public async Task TestCreatePost_ThenRenameIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var cmd = new IManageCommand.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Attempt to rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var cmd = new IManageCommand.Rename(newSlug);
        response = await _client.ApiPostJsonAsync($"/blog/{slugName}/rename", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
   
    [Fact]
    public async Task TestCreatePost_ThenRename_ThenFetchIt_FailsForOldName()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var cmd = new IManageCommand.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("Attempt to fetch post");
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}", token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_ThenViewIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var cmd = new IManageCommand.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("Fetch post");
        slugName = Contents.ComputeSlugName(newSlug);
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}", token);
        response.EnsureSuccessStatusCode();
        var contents = await response.ReadAsJsonAsync<Contents>();
        Assert.Equal(post, contents);
    }
#endregion
#region Change post permissions tests
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Change perms");
        var cmd = new IManageCommand.SetPermissions(new IManageCommand.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Change perms");
        var cmd = new IManageCommand.SetPermissions(new IManageCommand.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonAsync($"/blog/{slugName}/permissions", cmd);
       Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenViewItPublicly()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Change perms");
        var cmd = new IManageCommand.SetPermissions(new IManageCommand.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("View post publicly");
        response = await _client.ApiGetAsync($"/blog/{slugName}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenMakeItPrivateAgain()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new IManageCommand.SetPermissions(new IManageCommand.Permissions
        {
            Public = true
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Change perms back");
        cmd = new IManageCommand.SetPermissions(new IManageCommand.Permissions());
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/permissions", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("Attempt to view post publicly");
        response = await _client.ApiGetAsync($"/blog/{slugName}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endregion
#region Change post author tests
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Change author");
        var cmd = new IManageCommand.SetAuthor(u2.Email);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/chauthor", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        var (u2, _) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Attempt to change author");
        var cmd = new IManageCommand.SetAuthor(u2.Email);
        response = await _client.ApiPostJsonAsync($"/blog/{slugName}/chauthor", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_FailsForInvalidNewAuthor()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Attempt to change author");
        var cmd = new IManageCommand.SetAuthor("@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@");
        response = await _client.ApiPostJsonAsync($"/blog/{slugName}/chauthor", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_TransfersOwnership()
    {
        var (_, token1) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token1, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        var (u2, token2) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Change author");
        var cmd = new IManageCommand.SetAuthor(u2.Email);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/chauthor", token1, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}", token2);
        response.EnsureSuccessStatusCode();
        
        _logger.LogInformation("Attempt to fetch post with old uid");
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}", token1);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endregion
#region Delete post tests
    [Fact]
    public async Task TestCreatePost_ThenDeleteIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Delete post");
        response = await _client.ApiDeleteWithBearerAsync($"/blog/{slugName}", token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenDeleteIt_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Delete post");
        response = await _client.ApiDeleteAsync($"/blog/{slugName}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenDeleteIt_DeletesIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog/-new", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        
        _logger.LogInformation("Delete post");
        response = await _client.ApiDeleteWithBearerAsync($"/blog/{slugName}", token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        
        _logger.LogInformation("Attempt to fetch");
        response = await _client.ApiGetWithBearerAsync($"/blog/{slugName}", token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endregion
}