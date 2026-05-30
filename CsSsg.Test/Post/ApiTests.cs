using System.Security.Claims;
using KotlinScopeFunctions;
using LanguageExt;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using ZiggyCreatures.Caching.Fusion;
using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Post;
using RepositoryExtensions = CsSsg.Src.Post.RepositoryExtensions;
using static CsSsg.Src.Post.IManageCommand;
using static CsSsg.Src.Post.RoutingExtensions;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.User;
using static CsSsg.Src.User.RoutingExtensions;
using CsSsg.Test.Db;
using CsSsg.Test.SharedTypes;
using CsSsg.Test.User;

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
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper));
        fixture.DbContextOptionsBuilder.UseLoggerFactory(_loggerFactory);
        _contextFactory = () => new AppDbContext(fixture.DbContextOptionsBuilder.Options);
        _logger = _loggerFactory.CreateLogger<ApiTests>();
    }

    private static int _nextUserId => Interlocked.Increment(ref _userCounter);
    private static int _nextPostId => Interlocked.Increment(ref _postCounter);

    private async Task<(string, ClaimsPrincipal)> _nextUserAsync(AppDbContext continueContext, CancellationToken token)
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        var user = new Request(Email: $"{nextUserId}@test!post", Password: $"test{nextUserId}");
        var (signupResult, signupClaims) = await DoPostUserSignupActionAsync(continueContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
        return (user.Email, signupClaims.ToIdentity());
    }

    #endregion

    #region Create post tests

    [Fact]
    public async Task TestCreatePost()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            inserted => _logger.LogInformation("insert success: {insertResult}", inserted),
            failCode => Assert.Fail($"insert failed: {failCode}")
        );
    }

    [Fact]
    public async Task TestCreatePost_ResolvesInsertDuplicates()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.RequireInsertSuccess(_logger);
        result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.RequireInsertSuccess(_logger);
    }

    [Fact]
    public async Task TestCreatePost_ThenFetchRenderedEntry()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);

        _logger.LogInformation("Fetch entry");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, cToken, dbContext, _cache, token);
        entry.IfNone(() => Assert.Fail("failed to fetch"));
    }

    [Fact]
    public async Task TestCreatePost_ThenFetchListing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var flag_User = RepositoryExtensions.ListingFilter.UserOnly;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (email, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);

        _logger.LogInformation("Fetch listing");
        var utcNow = DateTime.UtcNow;
        var entryItr = await DoGetAllAvailableBlogEntriesAsync(user, flag_User, 2, utcNow,
            dbContext, _cache, token);
        var entries = entryItr.ToList();
        Assert.Single(entries);
        var entry = entries.First();
        Assert.Equal(post.Title, entry.Title);
        Assert.Equal(inserted, entry.Slug);
        Assert.Equal(email, entry.AuthorHandle);
        Assert.DoesNotContain("public", entry.Tags);
    }

    [Fact]
    public async Task TestCreatePost_ThenFetchPublicListing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var flag_User = RepositoryExtensions.ListingFilter.UserOnly;
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();
        var nullUser = AuthenticationExtensions.NullUser;

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            inserted => _logger.LogInformation("insert success: {insertResult}", inserted),
            failCode => Assert.Fail($"insert failed: {failCode}"));

        _logger.LogInformation("Fetch public listing");
        var utcNow = DateTime.UtcNow;
        var entryTitles =
            (await DoGetAllAvailableBlogEntriesAsync(nullUser, flag_User, 1, utcNow, dbContext, _cache, token))
            .Select(entry => entry.Title);
        Assert.DoesNotContain(post.Title, entryTitles);
    }

    [Fact]
    public async Task TestCreatePosts_ThenCheckListingFlags()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user1) = await _nextUserAsync(dbContext, token);
        var (_, user2) = await _nextUserAsync(dbContext, token);
        var nullUser = AuthenticationExtensions.NullUser;

        var entries = await AsyncEnumerable.Range(0, 4).Select(async (i, _, _) =>
        {
            var doPublic = (i & 1) != 0;
            var whichUid = ((i & 2) == 0 ? user1 : user2).RequireUid();
            _logger.LogInformation("post {}: create", i);
            var post = new Contents($"Hello {_nextPostId}", "# World");
            var result = await DoSubmitBlogEntryCreationAsync(post, whichUid, dbContext, _cache, rLogger, token);
            var slug = result.RequireInsertSuccess(_logger);
            var cToken = new RepositoryExtensions.ConcurrencyToken();

            _logger.LogInformation("post {}: chperm", i);
            if (doPublic)
            {
                var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
                var manageResult = await DoSubmitChangeTagsForNameAsync(slug, whichUid, command, cToken,
                    dbContext, _cache, rLogger, token);
                manageResult.Match(
                    failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
                    () => _logger.LogInformation("chperm success")
                );
            }

            return new { slug, post };
        }).ToListAsync(token);
        var allSlugs = entries.Select(e => e.slug).ToList();
        var utcNow = DateTime.UtcNow;

        _logger.LogInformation("slugs: {}", string.Join(" ", allSlugs));

        var flags_dfl = default(RepositoryExtensions.ListingFilter);
        var flags_uo = RepositoryExtensions.ListingFilter.UserOnly;
        var flags_tags = RepositoryExtensions.ListingFilter.Tags;
        var flags_uo_tags = flags_uo | flags_tags;


        var tab = new[]
        {
            new { name = "u1_uo_tag", expIndices = new[] { 1 }, user = user1, flags = flags_uo_tags },
            new { name = "u1_uo", expIndices = new[] { 0, 1 }, user = user1, flags = flags_uo },
            new { name = "u1_dfl", expIndices = new[] { 0, 1, 3 }, user = user1, flags = flags_dfl },
            new { name = "u2_uo_tag", expIndices = new[] { 3 }, user = user2, flags = flags_uo_tags },
            new { name = "u2_uo", expIndices = new[] { 2, 3 }, user = user2, flags = flags_uo },
            new { name = "u2_dfl", expIndices = new[] { 1, 2, 3 }, user = user2, flags = flags_dfl },
            new { name = "null_uo", expIndices = new int[] { }, user = nullUser, flags = flags_uo },
            new { name = "null_tag", expIndices = new[] { 1, 3 }, user = nullUser, flags = flags_tags },
            // this should produce the same sequence as null_p
            new { name = "null_dfl", expIndices = new[] { 1, 3 }, user = nullUser, flags = flags_dfl },
        };

        await Assert.AllAsync(tab, async arg =>
        {
            // Assert.AllAsync has a `foreach () { await }` so we don't need a critical section here
            var got = (await DoGetAllAvailableBlogEntriesAsync(arg.user, arg.flags, tab.Length, utcNow,
                    dbContext, _cache, token))
                .Select(entry => entry.Slug)
                .Where(allSlugs.Contains);
            var exp = allSlugs.SelectIndices(arg.expIndices);
            Assert.Equal(exp.Order(), got.Order());
        });
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
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents(newTitle, "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        result.Match(
            inserted => Assert.Fail($"expected failCode=Conflict but got inserted={inserted}"),
            failCode => Assert.Equal(expFailCode, failCode)
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
            inserted => Assert.Fail($"expected failCode=Conflict but got inserted={inserted}"),
            failCode => Assert.Equal(Failure.NotPermitted, failCode));
    }

    [Fact]
    public async Task TestCreatePost_ThenFetchRenderedEntry_FailsForConcurrencyConflict()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        (await DoSubmitChangeTagsForNameAsync(inserted, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .IfSome(f => Assert.Fail($"chperm: {f}"));

        _logger.LogInformation("Fetch entry");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, cToken, dbContext, _cache, token);
        entry.IfSome(_ => Assert.Fail("got content but shouldn't've"));
    }

    #endregion

    #region Fetch post tests

    [Fact]
    public async Task TestFetchEntry_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var token = CancellationToken.None;
        var entry = await DoGetRenderedBlogEntryForNameAsync(IMPOSSIBLE_SLUG, cToken, dbContext, _cache, token);
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
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();


        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Update post");
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false, cToken,
            dbContext, _cache, rLogger, token);
        updateResult.IfSome(failCode => Assert.Fail($"update failed: {failCode}"));
    }

    [Fact]
    public async Task TestCreatePost_ThenUpdateIt_ThenFetchRenderedEntry()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Update post");
        // change not just the body but the title too to ensure the slug doesn't change on update
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false, cToken,
            dbContext, _cache, rLogger, token);
        updateResult.IfSome(failCode => Assert.Fail($"update failed: {failCode}"));

        _logger.LogInformation("Fetch entry");
        // cTok only changes on permission update so no need to increment it for normal update
        var entry = await DoGetRenderedBlogEntryForNameAsync(slug, cToken, dbContext, _cache, token);
        entry.Match(
            contents =>
            {
                var title = contents.Title;
                Assert.DoesNotContain("Hello", title);
                Assert.Contains("Goodbye", title);
            },
            () => Assert.Fail("failed to fetch")
        );
    }

    [Fact]
    public async Task TestUpdatePost_FailsForNonexistent()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Update post");
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, newContents, false,
            cToken,
            dbContext, _cache, rLogger, token);
        updateResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail("failed to error")
        );
    }

    [Fact]
    public async Task TestCreatePost_ThenUpdateIt_FailsForConcurrenyConflict()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        (await DoSubmitChangeTagsForNameAsync(slug, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .IfSome(f => Assert.Fail($"chperm: {f}"));

        _logger.LogInformation("Update post");
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false, cToken,
            dbContext, _cache, rLogger, token);
        updateResult.Match(f => Assert.Equal(Failure.Conflict, f),
            () => Assert.Fail("expected failCode=Conflict"));
    }

    #endregion

    #region Fetch post manage page tests

    [Fact]
    public async Task TestCreatePost_ThenFetchItsManagePage_PropagatingSuppliedPermissionValues()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        var perms = new PostTags
        {
            Visibility = PostVisibility.Public // this contradicts defaults but is useful for verifying propagation
        };
        var mResult = await DoGetManagePageForNameAndPermissionAsync(inserted, uid, perms, cToken,
            dbContext, _cache, token);
        Assert.Equal(post.Title, mResult.Title);
        Assert.Equal(post.Body.Length, mResult.ContentLength);
        Assert.Equal(perms, mResult.Tags);
    }

    [Fact]
    public async Task TestManagePage_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;

        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var perms = new IManageCommand.PostTags();
        var message = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await DoGetManagePageForNameAndPermissionAsync(IMPOSSIBLE_SLUG, Guid.Empty, perms, cToken,
                dbContext, _cache, token);
        });
        Assert.Contains("content is missing", message.Message);
    }

    [Fact]
    public async Task TestCreatePost_ThenFetchItsManagePage_FailsForConcurrencyConflict()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        (await DoSubmitChangeTagsForNameAsync(inserted, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .IfSome(f => Assert.Fail($"chperm: {f}"));

        var perms = new PostTags();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DoGetManagePageForNameAndPermissionAsync(inserted, uid, perms, cToken, dbContext, _cache, token));
    }

    #endregion

    #region Rename post tests

    [Fact]
    public async Task TestCreatePost_ThenRenameIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new IManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => _logger.LogInformation("rename success: {newName}", newName),
            failCode => Assert.Fail($"rename failed: {failCode}"));
    }

    [Fact]
    public async Task TestCreatePost_ThenRename_ThenFetchIt_FailsForOldName()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new IManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => newName.Also(_ => _logger.LogInformation("rename success: {newName}", newName)),
            failCode => Assert.Fail($"rename failed: {failCode}"));

        _logger.LogInformation("Attempt to fetch old entry");
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, cToken, dbContext, _cache, token);
        entry.IfSome(_ => Assert.Fail("fetched by old name without error"));
    }

    [Fact]
    public async Task TestCreatePost_ThenRenameIt_ThenFetchIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new IManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        var renamed = manageResult.Match(
            newName => newName.Also(_ => _logger.LogInformation("rename success: {newName}", newName)),
            failCode => "".Also(_ => Assert.Fail($"rename failed: {failCode}"))
        )!;

        _logger.LogInformation("Fetch entry");
        var entry = await DoGetRenderedBlogEntryForNameAsync(renamed, cToken, dbContext, _cache, token);
        entry.Match(
            contents =>
            {
                var title = contents.Title;
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
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Create second post");
        post = new Contents($"Hello {_nextPostId}", "# World");
        insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted2 = insertResult.RequireInsertSuccess(_logger);

        _logger.LogInformation("Rename entry");
        var command = new IManageCommand.Rename(inserted2);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        var newName = manageResult.Match(
            newName => newName.Also(_ => _logger.LogInformation("rename success: {newName}", newName)),
            failCode => "".Also(_ => Assert.Fail($"rename failed: {failCode}"))
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
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var command = new IManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"),
            failCode => Assert.Equal(Failure.NotFound, failCode));
    }

    [Fact]
    public async Task TestCreatePost_ThenRename_ThenFetchIt_FailsForConcurrencyConflict()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        (await DoSubmitChangeTagsForNameAsync(inserted, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .IfSome(f => Assert.Fail($"chperm: {f}"));

        _logger.LogInformation("Attempt to rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail("expected failure but got success"),
            failCode => Assert.Equal(Failure.Conflict, failCode));
    }

    #endregion

    #region Change post tags tests

    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
    }

    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenCheckPerms()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
        cToken = cToken.Next();

        _logger.LogInformation("Fetch entry public perms");
        var perms = await dbContext.GetPermissionsForContentAsync(inserted, token);
        Assert.NotNull(perms);
        Assert.Contains(RepositoryExtensions.TAG_PUBLIC, perms.Tags);
        Assert.Equal(cToken, perms.ConcurrencyToken);
    }

    // currently, revoking public only does cache invalidation but leave it in unit tests for branch coverage
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenMakeItPrivateAgain()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
        cToken = cToken.Next();

        _logger.LogInformation("Reset permissions");
        command = new SetTags(new PostTags());
        manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );
        cToken = cToken.Next();

        _logger.LogInformation("Fetch entry perms");
        var perms = await dbContext.GetPermissionsForContentAsync(inserted, token);
        Assert.NotNull(perms);
        Assert.Equal(cToken, perms.ConcurrencyToken);
    }

    [Fact]
    public async Task TestChangePostPermissions_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var command = new SetTags(new PostTags());
        var manageResult = await DoSubmitChangeTagsForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail($"expected error but got success"));
    }

    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenMakeItPrivateAgain_FailsForConcurrencyConflict()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );

        _logger.LogInformation("Attempt to reset permissions");
        command = new SetTags(new PostTags());
        manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.Conflict, failCode),
            () => Assert.Fail("expected failure but got success")
        );
    }

    #endregion

    #region Change post author tests

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();
        var (email2, _) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change entry author");
        var command = new IManageCommand.SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => newName.Also(_ => _logger.LogInformation("change author success: {newName}", newName)),
            failCode => "".Also(_ => Assert.Fail($"change author failed: {failCode}")));
    }

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_ThenCheckPermissions()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var (email2, user2) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();
        var uid2 = user2.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change entry author");
        var command = new IManageCommand.SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => newName.Also(_ => _logger.LogInformation("change author success: {newName}", newName)),
            failCode => "".Also(_ => Assert.Fail($"change author failed: {failCode}")));
        cToken = cToken.Next();

        _logger.LogInformation("Check new permissions");
        var perms = await dbContext.GetPermissionsForContentAsync(inserted, token);
        Assert.NotNull(perms);
        Assert.Equal(uid2, perms.AuthorId);
        Assert.Equal(cToken, perms.ConcurrencyToken);
    }

    [Fact]
    public async Task TestChangePostAuthor_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Rename entry");
        var command = new IManageCommand.SetAuthor("-");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var manageResult = await DoSubmitSetAuthorForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"),
            failCode => Assert.Equal(Failure.NotFound, failCode));
    }

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_FailsForInvalidNewAuthor()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Rename entry");
        var command = new IManageCommand.SetAuthor("-");
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"),
            failCode => Assert.Equal(Failure.NotFound, failCode));
    }

    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_FailsForConcurrencyConflict()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var (u2, _) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        var pCommand = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var pManageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, pCommand, cToken,
            dbContext, _cache, rLogger, token);
        pManageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );

        _logger.LogInformation("Attempt to change author");
        var command = new SetAuthor(u2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail($"expected failCode=Conflict but got newName={newName}"),
            failCode => Assert.Equal(Failure.Conflict, failCode));
    }

    #endregion

    #region Delete post tests

    [Fact]
    public async Task TestCreatePost_ThenDeleteIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Delete post");
        var manageResult = await DoDeleteBlogEntryAsync(inserted, false, uid, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.IfSome(failCode => Assert.Fail($"delete failed: {failCode}"));
    }

    [Fact]
    public async Task TestCreatePost_ThenDelete_ThenFetchIt_Fails()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Delete post");
        var manageResult = await DoDeleteBlogEntryAsync(inserted, false, uid, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.IfSome(failCode => Assert.Fail($"delete failed: {failCode}"));
        var fetchResult = await DoGetRenderedBlogEntryForNameAsync(inserted, cToken, dbContext, _cache, token);
        fetchResult.IfSome(_ => Assert.Fail("fetch succeeded when it shouldn't've"));
    }

    [Fact]
    public async Task TestDeletePost_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Delete post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var manageResult = await DoDeleteBlogEntryAsync(IMPOSSIBLE_SLUG, false, Guid.Empty, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail("expected failCode=NotFound but got success"));
    }

    [Fact]
    public async Task TestCreatePost_ThenDeleteIt_FailsForConcurrencyConflict()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        var pCommand = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var pManageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, pCommand, cToken,
            dbContext, _cache, rLogger, token);
        pManageResult.Match(
            failCode => "".Also(_ => Assert.Fail($"chperm failed: {failCode}")),
            () => _logger.LogInformation("chperm success")
        );

        _logger.LogInformation("Delete post");
        var manageResult = await DoDeleteBlogEntryAsync(inserted, false, uid, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.Match(f => Assert.Equal(Failure.Conflict, f),
            () => Assert.Fail("delete succeeded when it shouldn't've"));
    }

    #endregion
}

internal static class InsertHandler
{
    extension(Either<Failure, string> insertResult)
    {
        internal string RequireInsertSuccess(ILogger logger, string op = "insert")
            => insertResult.Match(
                inserted =>
                    inserted.Also(_ => logger.LogInformation("{op} success: {insertResult}", op, inserted)),
                failCode => "".Also(_ => Assert.Fail($"{op} failed: {failCode}"))
            );
    }
}