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
    // these two must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;
    private static int _postCounter;
    
    const string IMPOSSIBLE_SLUG = "-"; // this slug can never appear because it is invalid

    public ApiTests(PostgresFixture fixture, ITestOutputHelper outputHelper)
    {
        _contextFactory = () => new AppDbContext(fixture.DbContextOptions);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper));
        _logger = _loggerFactory.CreateLogger<ApiTests>();
    }
    
    private static int _nextUserId =>  Interlocked.Increment(ref _userCounter);
    private static int _nextPostId =>  Interlocked.Increment(ref _postCounter);

    private async Task<(string, Guid)> _nextUserAsync(AppDbContext continueContext, CancellationToken token)
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
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
        Assert.Equal(post.Title, entry.Title);
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Fail($"insert failed: {failCode}"),
            inserted => _logger.LogInformation("insert success: {insertResult}", inserted)
        );
        _logger.LogInformation("Fetch public listing");
        var utcNow = DateTime.UtcNow;
        var entryTitles =
            (await DoGetAllAvailableBlogEntriesAsync(null, 1, utcNow, dbContext, _cache, token))
            .Select(entry => entry.Title);
        Assert.DoesNotContain(post.Title, entryTitles);
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
        var post = new Contents($"hello {_nextPostId}", "world");
        var badUid = Guid.Empty;
        var result = await DoSubmitBlogEntryCreationAsync(post, badUid, dbContext, _cache, rLogger, token);
        result.Match(
            failCode => Assert.Equal(Failure.NotPermitted, failCode),
            inserted => Assert.Fail($"expected failCode=Conflict but got inserted={inserted}"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenFetchEntry_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        
        _logger.LogInformation("Update post");
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        
        _logger.LogInformation("Update post");
        // change not just the body but the title too to ensure the slug doesn't change on update
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
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
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        
        _logger.LogInformation("Update post");
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
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
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, newContents, false,
            dbContext, _cache, rLogger, token);
        updateResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail("failed to error")
        );
    }
#endregion
#region Fetch post manage page tests
    [Fact]
    public async Task TestCreatePost_ThenFetchItsManagePage_PropagatingSuppliedPermissionValues()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        var perms = new ManageCommand.Permissions
        {
            Public = true // this contradicts defaults but is useful for verifying propagation
        };
        var mResult = await DoGetManagePageForNameAndPermissionAsync(inserted, uid, perms, dbContext, _cache, token);
        Assert.Equal(post.Title, mResult.Title);
        Assert.Equal(post.Body.Length, mResult.ContentLength);
        Assert.Equal(perms, mResult.Permissions);
    }
    
    [Fact]
    public async Task TestManagePage_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;

        var perms = new ManageCommand.Permissions();
        var message = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await DoGetManagePageForNameAndPermissionAsync(IMPOSSIBLE_SLUG, Guid.Empty, perms, dbContext, _cache, token);
        });
        Assert.Contains("missing entry", message.Message);
    }
#endregion
#region Rename post tests
    [Fact]
    public async Task TestCreatePost_ThenRenameIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new ManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"rename failed: {failCode}")),
            newName => newName.Also(_ => _logger.LogInformation("rename success: {newName}", newName))
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRename_ThenFetchIt_FailsForOldName()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new ManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Fail($"rename failed: {failCode}"),
            newName => newName.Also(_ => _logger.LogInformation("rename success: {newName}", newName))
        );
        
        _logger.LogInformation("Attempt to fetch old entry");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, uid, dbContext, _cache, token);
        entry.Match(
            _ => Assert.Fail("fetched by old name without error"),
            () => { }
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_ThenFetchIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new ManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, dbContext, _cache, rLogger, token);
        var renamed = manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"rename failed: {failCode}")),
            newName => newName.Also(_ => _logger.LogInformation("rename success: {newName}", newName))
        )!;
        
        _logger.LogInformation("Fetch entry");
        var entry = await DoGetRenderedBlogEntryForNameAsync(renamed, uid, dbContext, _cache, token);
        entry.Match(
            contents =>
            {
                var (title, _) = contents;
                Assert.Contains("Hello", title);
            },
            () => Assert.Fail("failed to fetch")
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenCreateAnotherOne_ThenRenameWithSameNameToInvokeDuplicateResolution()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
        
        _logger.LogInformation("Create second post");
        post = new Contents($"Hello {_nextPostId}", "# World");
        insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted2 = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            ins2 => ins2.Also(_ => _logger.LogInformation("insert success: {ins2}", ins2))
        );
        
        _logger.LogInformation("Rename entry");
        var command = new ManageCommand.Rename(inserted2);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, dbContext, _cache, rLogger, token);
        var newName = manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"rename failed: {failCode}")),
            newName => newName.Also(_ => _logger.LogInformation("rename success: {newName}", newName))
        );
        Assert.Contains(".", newName);
    }
    
    [Fact]
    public async Task TestRenamePost_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new ManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRenameIt_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new ManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, Guid.Empty, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotPermitted, failCode),
            newName => Assert.Fail($"expected failCode=Conflict but got newName={newName}"));
    }
#endregion
#region Change post permissions tests
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Change permissions");
        var command = new ManageCommand.SetPermissions(new ManageCommand.Permissions
        {
            Public = true
        });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(inserted, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
    }
    
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenFetchItPublicly()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Change permissions");
        var command = new ManageCommand.SetPermissions(new ManageCommand.Permissions
        {
            Public = true
        });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(inserted, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
        
        _logger.LogInformation("Fetch entry publicly");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, Guid.Empty, dbContext, _cache, token);
        entry.Match(
            contents =>
            {
                var (title, _) = contents;
                Assert.Contains("Hello", title);
            },
            () => Assert.Fail("failed to fetch")
        );
    }
    
    // currently, revoking public only does cache invalidation but leave it in unit tests for branch coverage
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenMakeItPrivateAgain()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Change permissions");
        var command = new ManageCommand.SetPermissions(new ManageCommand.Permissions
        {
            Public = true
        });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(inserted, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
        
        _logger.LogInformation("Reset permissions");
        command = new ManageCommand.SetPermissions(new ManageCommand.Permissions { });
        manageResult = await DoSubmitChangePermissionsForNameAsync(inserted, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
    }
    
    [Fact]
    public async Task TestChangePostPermissions_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        var command = new ManageCommand.SetPermissions(new ManageCommand.Permissions { });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail($"expected error but got success"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeIt_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        var command = new ManageCommand.SetPermissions(new ManageCommand.Permissions { });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(inserted, Guid.Empty, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotPermitted, failCode),
            () => Assert.Fail("expected failCode=NotPermitted but got success"));
    }
#endregion
#region Change post author tests
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        var (email2, _) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted)));
            
        _logger.LogInformation("Change entry author");
        var command = new ManageCommand.SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"change author failed: {failCode}")),
            newName => newName.Also(_ => _logger.LogInformation("change author success: {newName}", newName)));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_ThenFetchIt_FailsForOldUid()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        var (email2, uid2) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Change entry author");
        var command = new ManageCommand.SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"change author failed: {failCode}")),
            newName => newName.Also(_ => _logger.LogInformation("change author success: {newName}", newName)));
        
        _logger.LogInformation("Attempt to fetch with old uid");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, uid, dbContext, _cache, token);
        entry.IfSome(_ => Assert.Fail("got content but shouldn't've"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_ThenFetchIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        var (email2, uid2) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Change entry author");
        var command = new ManageCommand.SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"change author failed: {failCode}")),
            newName => newName.Also(_ => _logger.LogInformation("change author success: {newName}", newName)));
        
        _logger.LogInformation("Attempt to fetch with old uid");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, uid2, dbContext, _cache, token);
        entry.IfNone(() => Assert.Fail("got content but shouldn't've"));
    }
    
    [Fact]
    public async Task TestChangePostAuthor_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Rename entry");
        var command = new ManageCommand.SetAuthor("-");
        var manageResult = await DoSubmitSetAuthorForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new ManageCommand.SetAuthor("-");
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, Guid.Empty, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotPermitted, failCode),
            newName => Assert.Fail($"expected failCode=Conflict but got newName={newName}"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_FailsForInvalidNewAuthor()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        );
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new ManageCommand.SetAuthor("-");
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"));
    }
#endregion
}