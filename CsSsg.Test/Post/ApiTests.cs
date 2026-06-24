using System.Security.Claims;
using LanguageExt;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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
        var slug = result.RequireSuccess(_logger, "create-post");
        
        var postId = await dbContext.Posts
            .Where(p => p.Slug == slug)
            .Select(p => p.Id)
            .SingleAsync(token);
        _ = await dbContext.PostRevisions
            .Where(r => r.PostId == postId)
            .SingleAsync(token);
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
        var slug1 = result.RequireSuccess(_logger, "create-post");
        result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug2 = result.RequireSuccess(_logger, "create-post");

        var slugs = new[] { slug1, slug2 } as IEnumerable<string>;

        var postIds = await dbContext.Posts
            .Where(p => slugs.Contains(p.Slug))
            .Select(p => p.Id)
            .ToListAsync(token);
        Assert.Equal(2, postIds.Count);
        var revIds = await dbContext.PostRevisions
            .Where(r => postIds.Contains(r.PostId))
            .Select(r => r.Id)
            .ToListAsync(token);
        Assert.Equal(2, revIds.Count);
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
        var inserted = result.RequireSuccess(_logger, "create-post");

        _logger.LogInformation("Fetch entry");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var entry = await DoGetRenderedBlogEntryForNameAsync(inserted, cToken, dbContext, _cache, token);
        entry.IfNone(() => Assert.Fail("failed to fetch"));
    }
    
    [InlineData(-1)]
    [InlineData(2)]
    [Theory]
    public async Task TestCreatePost_ThenFetchRenderedEntry_FailsForInvalidRevision(int revNum)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireSuccess(_logger, "create-post");

        _logger.LogInformation("Attempt to fetch invalid revision");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var entry = await FetchMarkdownAsync(inserted, cToken, dbContext, _cache, token, revNum);
        entry.IfSome(_ => Assert.Fail("expected to fail"));
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
        var inserted = result.RequireSuccess(_logger, "create-post");
        
        _logger.LogInformation("Fetch listing");
        var utcNow = DateTime.UtcNow;
        var entryItr = await DoGetAllAvailableBlogEntriesAsync(user, flag_User, 2, utcNow,
            dbContext, _cache, token);
        var entries = entryItr.ToList();
        Assert.Single(entries);
        var entry = entries.First();
        Assert.Equal(post.Title, entry.LatestTitle);
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
        result.RequireSuccess(_logger, "create-post");

        _logger.LogInformation("Fetch public listing");
        var utcNow = DateTime.UtcNow;
        var entryTitles =
            (await DoGetAllAvailableBlogEntriesAsync(nullUser, flag_User, 1, utcNow, dbContext, _cache, token))
            .Select(entry => entry.LatestTitle);
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
            var slug = result.RequireSuccess(_logger, "create-post");
            var cToken = new RepositoryExtensions.ConcurrencyToken();

            _logger.LogInformation("post {}: chtag", i);
            if (doPublic)
            {
                var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
                var manageResult = await DoSubmitChangeTagsForNameAsync(slug, whichUid, command, cToken,
                    dbContext, _cache, rLogger, token);
                manageResult.RequireSuccess(_logger, "chtag");
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
        result.RequireFailure(_logger, "insert", (Failure)expFailCode);
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
        result.RequireFailure(_logger, "create-post", Failure.NotPermitted);
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
        var inserted = result.RequireSuccess(_logger, "create-post");
        
        _logger.LogInformation("Change permissions to increment permissions version on db side");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        (await DoSubmitChangeTagsForNameAsync(inserted, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .RequireSuccess(_logger, "chtag");

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
        var slug = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Update post");
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false, cToken,
            dbContext, _cache, rLogger, token);
        updateResult.IfSome(failCode => Assert.Fail($"update failed: {failCode}"));
        
        var postId = await dbContext.Posts
            .Where(p => p.Slug == slug)
            .Select(p => p.Id)
            .SingleAsync(token);
        var revIds = await dbContext.PostRevisions
            .Where(r => r.PostId == postId)
            .Select(r => r.Id)
            .ToListAsync(token);
        Assert.Equal(2, revIds.Count);
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
        var slug = insertResult.RequireSuccess(_logger, "create-post");
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
    public async Task TestCreatePost_ThenUpdateIt_ThenFetchRevisions()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var r1Lengths = (Revision: 1, Title: post.Title.Length, Body: post.Body.Length);

        _logger.LogInformation("Update post");
        // change not just the body but the title too to ensure the slug doesn't change on update
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false, cToken,
            dbContext, _cache, rLogger, token);
        updateResult.IfSome(failCode => Assert.Fail($"update failed: {failCode}"));
        var r2Lengths = (Revision: 2, Title: newContents.Title.Length, Body: newContents.Body.Length);

        _logger.LogInformation("Fetch revision summaries");
        var revsResult = await DoGetRevisionsForContentAsync(slug, cToken, dbContext, _cache, token);
        var revs = revsResult.RequireSuccess(_logger, "fetch-revisions");
        
        var revMeta = revs
            .OfType<Revision>()
            .Select(r => (Revison: r.Number, Title: r.Title.Length, Body: r.ContentLength));
        var exp = new[] { r2Lengths, r1Lengths }.AsEnumerable();
        Assert.Equal(exp, revMeta);

        _logger.LogInformation("Fetch revision data");
        var revContents = await AsyncEnumerable.Range(1, 2).Select(async (r, _, _) =>
            (await FetchMarkdownAsync(slug, cToken, dbContext, _cache, token, r))
            .Match(c => c, 
                () => throw new InvalidOperationException($"rev {r} fetch failed")
            ))
            .ToListAsync(token);
        var expRevContents = new[] { post, newContents };
        Assert.Equal(expRevContents, revContents, ContentsEqualityComparer.Instance);
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
            cToken, dbContext, _cache, rLogger, token);
        updateResult.RequireFailure(_logger, "update", Failure.NotFound);
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
        var slug = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        (await DoSubmitChangeTagsForNameAsync(slug, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .IfSome(f => Assert.Fail($"chtag: {f}"));

        _logger.LogInformation("Update post");
        var newContents = new Contents($"Goodbye {_nextPostId}", "# Planet");
        var updateResult = await DoSubmitBlogEntryEditForNameAsync(slug, uid, newContents, false, cToken,
            dbContext, _cache, rLogger, token);
        updateResult.RequireFailure(_logger, "update", Failure.Conflict);
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
        var inserted = result.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        var perms = new PostTags
        {
            Visibility = PostVisibility.Public // this contradicts defaults but is useful for verifying propagation
        };
        var mResult = await DoGetManagePageForNameAndPermissionAsync(inserted, uid, perms, cToken,
            dbContext, _cache, token);
        Assert.Equal(perms, mResult.Tags);
    }
    
    [Fact]
    public async Task TestCreatePost_ThenFetchItsManagePage_FetchesRevisions()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        var perms = new PostTags
        {
            Visibility = PostVisibility.Public // this contradicts defaults but is useful for verifying propagation
        };
        var mResult = await DoGetManagePageForNameAndPermissionAsync(inserted, uid, perms, cToken,
            dbContext, _cache, token);
        // reminder: revision fetch has order by descending
        var lastRev = mResult.Revisions.OfType<Revision>().ToList()[0];
        Assert.Equal(post.Title, lastRev.Title);
        Assert.Equal(post.Body.Length, lastRev.ContentLength);
    }

    [Fact]
    public async Task TestManagePage_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;

        var cToken = new RepositoryExtensions.ConcurrencyToken();
        var perms = new IManageCommand.PostTags();
        var ex = await Assert.ThrowsAsync<FailureException>(() =>
            DoGetManagePageForNameAndPermissionAsync(IMPOSSIBLE_SLUG, Guid.Empty, perms, cToken,
                dbContext, _cache, token)
        );
        Assert.Equal(Failure.NotFound, ex.Code);
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
        var inserted = result.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        (await DoSubmitChangeTagsForNameAsync(inserted, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .IfSome(f => Assert.Fail($"chtag: {f}"));

        var perms = new PostTags();
        var ex = await Assert.ThrowsAsync<FailureException>(() =>
            DoGetManagePageForNameAndPermissionAsync(inserted, uid, perms, cToken, dbContext, _cache, token));
        Assert.Equal(Failure.Conflict, ex.Code);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new IManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "rename");
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new IManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "rename");

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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new IManageCommand.Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        var renamed = manageResult.RequireSuccess(_logger, "rename");

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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Create second post");
        post = new Contents($"Hello {_nextPostId}", "# World");
        insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted2 = insertResult.RequireSuccess(_logger, "create-post");

        _logger.LogInformation("Rename entry");
        var command = new IManageCommand.Rename(inserted2);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        var newName = manageResult.RequireSuccess(_logger, "rename");
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
        manageResult.RequireFailure(_logger, "rename", Failure.NotFound);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        (await DoSubmitChangeTagsForNameAsync(inserted, uid, new SetTags(new PostTags(PostVisibility.Public)), cToken,
                dbContext, _cache, rLogger, token))
            .IfSome(f => Assert.Fail($"chtag: {f}"));

        _logger.LogInformation("Attempt to rename entry");
        var newSlug = $"<Hello -{_nextPostId}>";
        var command = new Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireFailure(_logger, "rename", Failure.Conflict);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chtag");
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chtag");
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chtag");
        cToken = cToken.Next();

        _logger.LogInformation("Reset permissions");
        command = new SetTags(new PostTags());
        manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chtag");
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
        manageResult.RequireFailure(_logger, "chtag", Failure.NotFound);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chtag");

        _logger.LogInformation("Attempt to reset permissions");
        command = new SetTags(new PostTags());
        manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireFailure(_logger, "chtag", Failure.Conflict);
    }

    [Fact]
    public async Task TestCreatePost_ThenSetTags_ThenFilterByExtraTags()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var flag_User = RepositoryExtensions.ListingFilter.UserOnly;
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();
        IList<string> auxTags = ["X"];

        _logger.LogInformation("Create posts and apply permissions");
        var entries = await AsyncEnumerable.Range(0, 2).Select(async (i, _, _) =>
        {
            var post = new Contents($"Hello {_nextPostId}", "# World");
            var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
            var inserted = result.RequireSuccess(_logger, "create-post");
            var cToken = new RepositoryExtensions.ConcurrencyToken();

            if (i % 2 == 1)
            {
                _logger.LogInformation("Change tags");
                var command = new SetTags(new PostTags { Tags = auxTags });
                var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
                    dbContext, _cache, rLogger, token);
                manageResult.RequireSuccess(_logger, "chtag");
            }

            return new { post.Title, inserted };
        }).ToListAsync(token);

        _logger.LogInformation("Fetch listing");
        var utcNow = DateTime.UtcNow;
        var entryTitles =
            (await DoGetAllAvailableBlogEntriesAsync(user, flag_User, 1, utcNow, dbContext, _cache, token, auxTags))
            .Select(entry => entry.LatestTitle)
            .ToList();
        Assert.Contains(entries[1].Title, entryTitles);
        Assert.DoesNotContain(entries[0].Title, entryTitles);
    }

    [InlineData(PostVisibility.Public, true)]
    [InlineData(PostVisibility.Unlisted, false)]
    [Theory]
    public async Task TestCreatePost_ThenChangeItsVisibility_ThenCheckListing(
        PostVisibility newVisibility, bool shouldExistInListing)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var flag_UserTag = RepositoryExtensions.ListingFilter.UserOnly | RepositoryExtensions.ListingFilter.Tags;
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var result = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var inserted = result.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions");
        var command = new SetTags(new PostTags(visibility: newVisibility));
        var manageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chtag");

        _logger.LogInformation("Fetch public listing");
        var utcNow = DateTime.UtcNow;
        var entryTitles =
            (await DoGetAllAvailableBlogEntriesAsync(user, flag_UserTag, 1, utcNow, dbContext, _cache, token))
            .Select(entry => entry.LatestTitle);
        if (shouldExistInListing)
            Assert.Contains(post.Title, entryTitles);
        else
            Assert.DoesNotContain(post.Title, entryTitles);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change entry author");
        var command = new IManageCommand.SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chauthor");
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change entry author");
        var command = new IManageCommand.SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "chauthor");
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
        manageResult.RequireFailure(_logger, "chauthor", Failure.NotFound);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Attempt to change entry author");
        var command = new IManageCommand.SetAuthor("-");
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireFailure(_logger, "chauthor", Failure.NotFound);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        var pCommand = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var pManageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, pCommand, cToken,
            dbContext, _cache, rLogger, token);
        pManageResult.RequireSuccess(_logger, "chtag");

        _logger.LogInformation("Attempt to change author");
        var command = new SetAuthor(u2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(inserted, uid, false, command, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireFailure(_logger, "chauthor", Failure.Conflict);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();
        
        // fetch ID before delete so we can confirm delete on DB side
        var postId = await dbContext.Posts
            .Where(p => p.Slug == inserted)
            .Select(p => p.Id)
            .SingleAsync(token);
        
        _logger.LogInformation("Delete post");
        var manageResult = await DoDeleteBlogEntryAsync(inserted, false, uid, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "delete");
        
        Assert.Equal(Guid.Empty, await dbContext.Posts
            .Where(p => p.Id == postId)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(token)
        );
        var revIds = await dbContext.PostRevisions
            .Where(r => r.PostId == postId)
            .Select(r => r.Id)
            .ToListAsync(token);
        Assert.Empty(revIds);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Delete post");
        var manageResult = await DoDeleteBlogEntryAsync(inserted, false, uid, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireSuccess(_logger, "delete");
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
        manageResult.RequireFailure(_logger, "delete", Failure.NotFound);
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
        var inserted = insertResult.RequireSuccess(_logger, "create-post");
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Change permissions to increment permissions version on db side");
        var pCommand = new SetTags(new PostTags(visibility: PostVisibility.Public));
        var pManageResult = await DoSubmitChangeTagsForNameAsync(inserted, uid, pCommand, cToken,
            dbContext, _cache, rLogger, token);
        pManageResult.RequireSuccess(_logger, "chtag");

        _logger.LogInformation("Delete post");
        var manageResult = await DoDeleteBlogEntryAsync(inserted, false, uid, cToken,
            dbContext, _cache, rLogger, token);
        manageResult.RequireFailure(_logger, "delete", Failure.Conflict);
    }

    #endregion
}

internal static class ResultExtensions
{
    private static void CheckType<T>()
    {
        if (!(typeof(T).IsEnum || typeof(T).IsValueType || typeof(T) == typeof(string)))
            throw new InvalidOperationException(
                "this validator is broken because it uses Assert.True(==, failMessage) instead of " +
                "Assert.Equal as for custom message in order to preserve parameter op");
    }
    
    extension<TR>(Either<Failure, TR> eitherResult)
    {
        internal TR RequireSuccess(ILogger logger, string op)
        {
            TR result = default!;
            eitherResult.Match(
                succ => result = succ,
                fail => Assert.Fail($"{op} failed: {fail}")
            );
            logger.LogInformation("{op} success: {insertResult}", op, result);
            return result;
        }
        
        internal void RequireFailure(ILogger logger, string op, Failure expCode)
        {
            CheckType<Failure>();
            eitherResult.Match(
                succ => Assert.Fail($"{op}: expected fail={expCode} but got success={succ}"),
                fail => Assert.True(fail == expCode, $"{op}: expected fail={expCode} but got {fail}")
            );
        }
    }

    extension(Option<Failure> maybeResult)
    {
        internal void RequireSuccess(ILogger logger, string op)
        {
            maybeResult.Match(
                f => Assert.Fail($"{op} failed: {f}"),
                () => logger.LogInformation($"{op} success"));
        }
        
        internal void RequireFailure(ILogger logger, string op, Failure expCode)
        {
            CheckType<Failure>();
            maybeResult.Match(
                f => Assert.True(f == expCode, "${op} failed but with code ${f}"),
                () => Assert.Fail($"{op} succeeded but expected fail={expCode}"));
        }
    }
}