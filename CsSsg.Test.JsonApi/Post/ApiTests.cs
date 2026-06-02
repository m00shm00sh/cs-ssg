using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using CsSsg.Src.Post;
using static CsSsg.Src.Post.IManageCommand;
using CsSsg.Src.User;
using Request = CsSsg.Src.User.Request;
using CsSsg.Test.Db;
using CsSsg.Test.JsonApi.Fixture;
using CsSsg.Test.JsonApi.Http;
using CsSsg.Test.Post;
using static CsSsg.Test.JsonApi.Http.RequestUtils;
using CsSsg.Test.SharedTypes;

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

    private static int _nextUserId => Interlocked.Increment(ref _userCounter);
    private static int _nextPostId => Interlocked.Increment(ref _postCounter);

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
        return new Request(Email: $"{nextUserId}@test!json!post", Password: $"test{nextUserId}");
    }

    #endregion

    #region Create and view post

    [Fact]
    public async Task TestCreatePost_RequiresAuth()
    {
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonAsync("/blog", post);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSignup_ThenCreatePost()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
    }

    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenCheckListing()
    {
        var (user, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch listing");
        response = await _client.ApiGetWithOptionsAsync("/blog", new GetOptions { Bearer = token });
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        var entry = entries
            .First(e => e.Slug == slugName
                        && e.Title == post.Title
                        && e.AuthorHandle == user.Email
                        && !e.IsPublic());
    }

    [Fact]
    public async Task TestSignup_ThenCreatePosts_ThenCheckListingForUser()
    {
        var (user1, token1) = await _nextSignedUpUserAsync(CancellationToken.None);
        var (user2, token2) = await _nextSignedUpUserAsync(CancellationToken.None);

        var entries = await AsyncEnumerable.Range(0, 4).Select(async (i, _, _) =>
        {
            var doPublic = (i & 1) != 0;
            var whichBearer = (i & 2) == 0 ? token1 : token2;
            _logger.LogInformation("post {}: create", i);
            var title = $"Hello _{_nextPostId}";
            var response = await _client.ApiPostJsonWithBearerAsync("/blog", whichBearer,
                new Contents(title, "# World"));
            response.EnsureSuccessStatusCode();
            var slugName = await response.ReadAsJsonAsync<string>();

            if (doPublic)
            {
                _logger.LogInformation("post {}: chperm", i);
                var cmd = new SetTags(new PostTags
                {
                    Visibility = PostVisibility.Public
                });
                response = await _client.ApiPostJsonWithBearerAsync(
                    $"/blog/{slugName}/tags", whichBearer, cmd);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            return slugName;
        }).ToListAsync();

        var blogUrl = "/blog";

        var tab = new[]
        {
            new { name = "listing_u1_uo", cookie = token1, qUser = user1.Email, expIndices = new[] { 0, 1 } },
            new { name = "listing_u1_u2o", cookie = token1, qUser = user2.Email, expIndices = new[] { 3 } }
        };

        await Assert.AllAsync(tab, async arg =>
        {
            var got = await FetchSlugs(arg.cookie, arg.qUser);
            var exp = entries.SelectIndices(arg.expIndices);
            Assert.Equal(exp.Order(), got.Order());
        });
        return;

        [SuppressMessage("ReSharper", "VariableHidesOuterVariable")]
        async Task<IEnumerable<string>> FetchSlugs(string? bearer, string? qUser)
        {
            var uri = blogUrl;
            if (qUser is not null)
                uri += "?user=" + WebUtility.UrlEncode(qUser);
            var response = await _client.ApiGetWithOptionsAsync(uri, new GetOptions { Bearer = bearer });
            var listing = await response.ReadAsJsonAsync<List<Entry>>();
            var got = listing!.Select(s => s.Slug);
            return got.Where(entries.Contains);
        }
    }

    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [Theory]
    public async Task TestSignup_ThenCreatePost_ThenViewIt(bool publicFetch, HttpStatusCode expStatus)
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}", new GetOptions
        {
            Bearer = !publicFetch ? token : null
        });
        if ((int)expStatus is >= 200 and <= 299)
        {
            response.EnsureSuccessStatusCode();
            var contents = await response.ReadAsJsonAsync<Contents>();
            contents = contents.WithDiscardedModifyTime();
            Assert.Equal(post, contents);
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
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();
        var fetchUrl = $"/blog/{slugName}";

        _logger.LogInformation("Fetch post");
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
    public async Task TestSignup_ThenCreatePost_ThenUpdatePostWithoutAuth_Fails()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Update");
        post = new Contents($"Hello {_nextPostId}", "# Universe");
        response = await _client.ApiPutJsonWithBearerAsync($"/blog/{slugName}", token, post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Check listing");
        response = await _client.ApiGetWithOptionsAsync("/blog", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        var _ = entries
            .First(e => e.Slug == slugName
                        && e.Title == post.Title
                        && !e.IsPublic());
    }

    [Fact]
    public async Task TestSignup_ThenCreatePost_ThenUpdateIt_ThenViewIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Update");
        post = new Contents($"Hello {_nextPostId}", "# Universe");
        response = await _client.ApiPutJsonWithBearerAsync($"/blog/{slugName}", token, post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var contents = await response.ReadAsJsonAsync<Contents>();
        contents = contents.WithDiscardedModifyTime();
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Fetch stats");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}/stats", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var stats = await response.ReadAsJsonAsync<Stats>();
        Assert.Equal(post.Title, stats.Title);
        Assert.Equal(post.Body.Length, stats.ContentLength);
        Assert.Equal(new PostTags(), stats.Tags, PostTagsEqualityComparer.Instance);
    }

    [Fact]
    public async Task TestCreatePost_ThenGetItsStats_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Attempt to fetch stats");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}/stats");
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var cmd = new IManageCommand.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Attempt to fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}", new GetOptions { Bearer = token });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenRenameIt_ThenViewIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var cmd = new IManageCommand.Rename(newSlug);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/rename", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Fetch post");
        slugName = Contents.ComputeSlugName(newSlug);
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}", new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var contents = await response.ReadAsJsonAsync<Contents>();
        contents = contents.WithDiscardedModifyTime();
        Assert.Equal(post, contents);
    }

    #endregion

    #region Change post tags tests

    [InlineData(PostVisibility.Public)]
    [InlineData(PostVisibility.Unlisted)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility(PostVisibility newVisibility)
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new SetTags(new PostTags
        {
            Visibility = newVisibility
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_RequiresAuth()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new SetTags(new PostTags
        {
            Visibility = PostVisibility.Public
        });
        response = await _client.ApiPostJsonAsync($"/blog/{slugName}/tags", cmd);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [InlineData(PostVisibility.Public)]
    [InlineData(PostVisibility.Unlisted)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility_ThenViewItPublicly(PostVisibility newVisibility)
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new SetTags(new PostTags
        {
            Visibility = newVisibility
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("View post publicly");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenMakeItPrivateAgain()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change perms");
        var cmd = new SetTags(new PostTags
        {
            Visibility = PostVisibility.Public
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Change perms back");
        cmd = new SetTags(new PostTags());
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Attempt to view post publicly");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    
    [InlineData(PostVisibility.Public, true)]
    [InlineData(PostVisibility.Unlisted, false)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility_ThenCheckPublicListing(
        PostVisibility newVisibility, bool shouldExistInListing)
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Change entry permissions");
        var cmd = new SetTags(new PostTags
        {
            Visibility = newVisibility
        });
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/tags", token, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Check public listing");
        response = await _client.ApiGetWithOptionsAsync("/blog");
        response.EnsureSuccessStatusCode();
        var entries = await response.ReadAsJsonAsync<List<Entry>>();
        if (shouldExistInListing)
        {
            Assert.NotNull(entries);
            Assert.NotEmpty(entries);
            _ = entries.First(e => e.Slug == slugName && e.Title == post.Title);
        }
        else
        {
            Assert.NotNull(entries);
            Assert.Throws<InvalidOperationException>(() =>
                entries.First(e => e.Slug == slugName && e.Title == post.Title));
        }
    }

    [Fact]
    public async Task TestCreatePost_ThenSetTags_ThenFilterByExtraTags()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);
        
        ICollection<string> auxTags = ["X"];

        _logger.LogInformation("Create posts and apply permissions");
        var entries = await AsyncEnumerable.Range(0, 2).Select(async (i, _, _) =>
        {
            _logger.LogInformation("Create post");
            var post = new Contents($"Hello {_nextPostId}", "# World");
            var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
            response.EnsureSuccessStatusCode();
            var slugName = await response.ReadAsJsonAsync<string>();

            if (i % 2 == 1)
            {
                _logger.LogInformation("Change tags");
                var cmd = new SetTags(new PostTags { Tags = auxTags });
                response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/tags", token, cmd);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            return new { post.Title, slugName };
        }).ToListAsync(CancellationToken.None);

        _logger.LogInformation("Fetch listing");
        var utcNow = DateTime.UtcNow;
        var response = await _client.ApiGetWithOptionsAsync(AddXtags("/blog"), new GetOptions { Bearer = token });
        response.EnsureSuccessStatusCode();
        var gotEntries = (await response.ReadAsJsonAsync<List<Entry>>())!
            .Select(e => e.Title)
            .ToList();
        Assert.Contains(entries[1].Title, gotEntries);
        Assert.DoesNotContain(entries[0].Title, gotEntries);
        return;

        string AddXtags(string baseUri)
            => baseUri + "?" + string.Join('&', auxTags.Select(t => $"xtags={WebUtility.UrlEncode(t)}"));
    }
    #endregion

    #region Change post author tests

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token1, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        var (u2, token2) = await _nextSignedUpUserAsync(CancellationToken.None);
        _logger.LogInformation("Change author");
        var cmd = new IManageCommand.SetAuthor(u2.Email);
        response = await _client.ApiPostJsonWithBearerAsync($"/blog/{slugName}/chauthor", token1, cmd);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Fetch post");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}", new GetOptions { Bearer = token2 });
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Attempt to fetch post with old uid");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}", new GetOptions { Bearer = token1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Delete post tests

    [Fact]
    public async Task TestCreatePost_ThenDeleteIt()
    {
        var (_, token) = await _nextSignedUpUserAsync(CancellationToken.None);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
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
        var response = await _client.ApiPostJsonWithBearerAsync("/blog", token, post);
        response.EnsureSuccessStatusCode();
        var slugName = await response.ReadAsJsonAsync<string>();

        _logger.LogInformation("Delete post");
        response = await _client.ApiDeleteWithBearerAsync($"/blog/{slugName}", token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        _logger.LogInformation("Attempt to fetch");
        response = await _client.ApiGetWithOptionsAsync($"/blog/{slugName}", new GetOptions { Bearer = token });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion
}

file static class TestHelpers
{
    extension(Contents c)
    {
        public Contents WithDiscardedModifyTime()
            => c with { LastModified = null };
    }
}