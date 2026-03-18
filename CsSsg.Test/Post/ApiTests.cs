using KotlinScopeFunctions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Db;
using CsSsg.Src.Post;
using static CsSsg.Src.Post.RoutingExtensions;
using CsSsg.Src.User;
using static CsSsg.Src.User.RoutingExtensions;

using CsSsg.Test.Db;

namespace CsSsg.Test.Post;

public class ApiTests : IClassFixture<PostgresFixture>
{
#region scaffolding
    private readonly Func<AppDbContext> _contextFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApiTests> _logger;
    private readonly IFusionCache _cache = new FusionCache(new FusionCacheOptions());
    // this must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;
    const string IMPOSSIBLE_SLUG = "-"; // this slug can never appear because it is invalid

    public ApiTests(PostgresFixture fixture, ITestOutputHelper outputHelper)
    {
        _contextFactory = () => new AppDbContext(fixture.DbContextOptions);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper));
        _logger = _loggerFactory.CreateLogger<ApiTests>();
    }

    private async Task<(string, Guid)> _nextUserAsync(AppDbContext continueContext, CancellationToken token)
    {
        var next = Interlocked.Increment(ref _userCounter);
        var nextUserId = $"{next + 1:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        var user = new Request(Email: $"{nextUserId}@test!post", Password: $"test{nextUserId}");
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(continueContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
        return (user.Email, signupUid);
    }
#endregion
#region Create post tests
    [Fact]
    public async Task TestCreatePost()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 01", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Fail($"insert failed: {failCode}"),
            inserted => _logger.LogInformation("insert success: {insertResult}", inserted)
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ResolvesInsertDuplicates()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 01a", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Fail($"insert failed: {failCode}"),
            inserted => _logger.LogInformation("insert success: {insertResult}", inserted)
        );
        result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Fail($"insert failed: {failCode}"),
            inserted =>
            {
                _logger.LogInformation("insert success: {insertResult}", inserted);
                Assert.Contains(".", inserted); // the dot will only appear in slug name on duplicate resolution
            }
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenFetchRenderedEntry()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 02", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
        
        _logger.LogInformation("Fetch entry");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, uid, dbContext, _cache, token);
        entry.IfNone(() =>
        {
            Assert.Fail("failed to fetch");
        });
    }
    
    [Fact]
    public async Task TestCreatePost_ThenFetchListing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (email, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 03", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
        
        _logger.LogInformation("Fetch listing");
        var utcNow = DateTime.UtcNow;
        var entryItr = await DoGetAllAvailableBlogEntriesAsync(uid, 2, utcNow, dbContext, _cache, token);
        var entries = entryItr.ToList();
        Assert.Single(entries);
        var entry = entries.First();
        Assert.Equal("Hello 03", entry.Title);
        Assert.Equal(inserted, entry.Slug);
        Assert.Equal(email, entry.AuthorHandle);
        Assert.False(entry.IsPublic);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenFetchPublicListing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 04", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Fail($"insert failed: {failCode}"),
            inserted => _logger.LogInformation("insert success: {insertResult}", inserted)
        );
        _logger.LogInformation("Fetch public listing");
        var utcNow = DateTime.UtcNow;
        var entryItr = await DoGetAllAvailableBlogEntriesAsync(null, 1, utcNow, dbContext, _cache, token);
        Assert.Empty(entryItr);
    }

    public static IList<object[]> InvalidContentTitles =
    [
        ["--", Failure.Conflict], // resolves to ""
        ["-", Failure.Conflict], // resolves to ""
        ["", Failure.Conflict],
        [new string('a', 255), Failure.TooLong] // the current TITLE_MAXLEN is 250
    ];
    
    [Theory]
    [MemberData(nameof(InvalidContentTitles))]
    public async Task TestCreatePost_FailsForEmptyTitle(string newTitle, object /* Failure */ expFailCode)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (email, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents(newTitle, "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Equal(expFailCode, failCode),
            inserted => Assert.Fail($"expected failCode=Conflict but got inserted={inserted}")
        );
    }
    
    [Fact]
    public async Task TestCreatePost_FailsForInvalidUser()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Create post");
        var post = new Contents("hello 06", "world");
        var badUid = Guid.Empty;
        var result = await DoSubmitBlogEntryCreationAsync(post, badUid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Equal(Failure.NotPermitted, failCode),
            inserted => Assert.Fail($"expected failCode=Conflict but got inserted={inserted}")
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenFetchEntry_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 04", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
        
        _logger.LogInformation("Attempt to fetch publicly");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, null, dbContext, _cache, token);
        entry.IfSome(_ => Assert.Fail("got content but shouldn't've"));
    }
#endregion
#region Fetch post tests
    [Fact]
    public async Task TestFetchEntry_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var entry = await DoGetRenderedBlogEntryForNameAsync(IMPOSSIBLE_SLUG, null, dbContext, _cache, token);
        entry.IfSome(_ => Assert.Fail("got content but shouldn't've"));
    }
#endregion
#region Update post tests
    [Fact]
    public async Task TestCreatePost_ThenUpdateIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 05", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        
        _logger.LogInformation("Update post");
        var newContents = new Contents("Goodbye 05", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false,
            dbContext, _cache, rLogger, token);
        updateResult.Match(
            failCode => Assert.Fail($"update failed: {failCode}"),
            () => _logger.LogInformation("update success")
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenUpdateIt_ThenFetchRenderedEntry()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 06", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        
        _logger.LogInformation("Update post");
        // change not just the body but the title too to ensure the slug doesn't change on update
        var newContents = new Contents("Goodbye 06", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false,
            dbContext, _cache, rLogger, token);
        updateResult.Match(
            failCode => Assert.Fail($"update failed: {failCode}"),
            () => _logger.LogInformation("update success")
        );
        
        _logger.LogInformation("Fetch entry");
        var entry = await DoGetRenderedBlogEntryForNameAsync(slug, uid, dbContext, _cache, token);
        entry.Match(
            contents =>
            {
                var (title, _) = contents;
                Assert.DoesNotContain("Hello", title);
                Assert.Contains("Goodbye", title);
            },
            () => Assert.Fail("failed to fetch")
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenUpdateIt_FailsForAnonymousUid()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents("Hello 06", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        
        _logger.LogInformation("Update post");
        var newContents = new Contents("Goodbye 06", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, Guid.Empty, newContents, false,
            dbContext, _cache, rLogger, token);
        updateResult.Match(
            failCode => Assert.Equal(Failure.NotPermitted, failCode),
            () => Assert.Fail("failed to error")
        );
    }
    
    [Fact]
    public async Task TestUpdatePost_FailsForNonexistent()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Update post");
        var newContents = new Contents("Goodbye 06", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, newContents, false,
            dbContext, _cache, rLogger, token);
        updateResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail("failed to error")
        );
    }
#endregion
}