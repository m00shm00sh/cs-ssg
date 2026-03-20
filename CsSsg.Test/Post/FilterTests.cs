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

public class FilterTests : IClassFixture<PostgresFixture>
{
#region scaffolding
    private readonly Func<AppDbContext> _contextFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApiTests> _logger;
    private readonly IFusionCache _cache = new FusionCache(new FusionCacheOptions());
    // these two must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;
    private static int _postCounter;
    
    public FilterTests(PostgresFixture fixture, ITestOutputHelper outputHelper)
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
        var user = new Request(Email: $"{nextUserId}@test!post.filter", Password: $"test{nextUserId}");
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(continueContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
        return (user.Email, signupUid);
    }
#endregion
#region ContentAccessPermissionFilter
    [Fact]
    public async Task TestCreatePost_ThenCheckPermissionsExistOnlyForCreator()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var cfLogger = _loggerFactory.CreateLogger<ContentAccessPermissionFilter>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;
        
        _logger.LogInformation("Fetch permissions");
        var filter = new ContentAccessPermissionFilter(cfLogger, _cache, dbContext);
        var perms = await filter.GetPermissionsAsync(slug, uid, token);
        perms.Match(
            p => Assert.Equal(AccessLevel.Write, p.AccessLevel),
            () => Assert.Fail("expected permissions but got none"));
        
        _logger.LogInformation("Fetch public permissions");
        var perms2 = await filter.GetPermissionsAsync(slug, null, token);
        perms2.IfSome(p => Assert.Fail($"expected no permissions but got {p.AccessLevel}"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenCheckPermissionsExistForAll()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var cfLogger = _loggerFactory.CreateLogger<ContentAccessPermissionFilter>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.Match(
            failCode => "".Also(_ => Assert.Fail($"insert failed: {failCode}")),
            inserted => inserted.Also(_ => _logger.LogInformation("insert success: {insertResult}", inserted))
        )!;

        _logger.LogInformation("Set permissions");
        var newPerms = new ManageCommand.Permissions
        {
            Public = true
        };
        var permsResult = await DoSubmitChangePermissionsForNameAsync(slug, uid, 
            new ManageCommand.SetPermissions(newPerms), dbContext, _cache, rLogger, token);
        permsResult.IfSome(failCode => Assert.Fail($"expected no error but got {failCode}"));
        
        _logger.LogInformation("Fetch permissions");
        var filter = new ContentAccessPermissionFilter(cfLogger, _cache, dbContext);
        var perms = await filter.GetPermissionsAsync(slug, uid, token);
        perms.Match(
            p => Assert.Equal(AccessLevel.WritePublic, p.AccessLevel),
            () => Assert.Fail("expected permissions but got none"));
        
        _logger.LogInformation("Fetch public permissions");
        var perms2 = await filter.GetPermissionsAsync(slug, null, token);
        perms2.Match(
            p => Assert.Equal(AccessLevel.Read, p.AccessLevel),
            () => Assert.Fail("expected permissions but got none"));
    }
#endregion
#region WritePermissionFilter

    public static IList<object?[]> TestDataForWritePermissionFilter()
    {
        List<object?[]> l = 
        [ // [ AccessLevel? existingAccess, bool createUser, Type<? : IResult>? ExpectedResult ]
            [ null, false, typeof(NotFound)], // anonymous user attempts to create new post
            [null, true, null], // known user attempts to create new post
        ];
        l.AddRange(
            ((AccessLevel[])[AccessLevel.Read, AccessLevel.None]).SelectMany(a => 
                    (bool[])[false, true],
                // (b=anonymous|known) user attempts to edit post given (a=RO|None) perms
                (a, b) => (object?[])[a, b, typeof(ForbidHttpResult)])
        );
        
        l.AddRange(
            ((AccessLevel[])[AccessLevel.Write, AccessLevel.WritePublic]).SelectMany(a => 
                    (bool[])[false, true],
                        // (b=anonymous|known) user attempts to edit post given (a=Wwrite|WritePublic) perms
                        (a, b) => (object?[])[a, b, null])
        );
        return l;
    }

    [Theory]
    [MemberData(nameof(TestDataForWritePermissionFilter))]
    public async Task TestWritePermissionFilter(object? oExistingAccessLevel, bool createUser, Type? expectedResult)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var uid = Guid.Empty;
        if (createUser)
            uid = (await _nextUserAsync(dbContext, token)).Item2;
        var wfLogger = _loggerFactory.CreateLogger<WritePermissionFilter>();
        var filter = new WritePermissionFilter(wfLogger, dbContext);
        var existingAccessLevel = (AccessLevel?)oExistingAccessLevel;
        var result = await filter.VerifyPermissionAsync(existingAccessLevel, "unittest.", uid, token);
        if (expectedResult is null)
            result.IfSome(r => Assert.Fail($"expected None but got {r}"));
        else
            result.Match(r => Assert.Equal(expectedResult, r.GetType()),
                () => Assert.Fail("expected {expectedResult} but got None"));
    }
#endregion
}