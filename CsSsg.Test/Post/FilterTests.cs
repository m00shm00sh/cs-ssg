using System.Security.Claims;
using CsSsg.Src.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Db;
using CsSsg.Src.Filters;
using CsSsg.Src.Post;
using static CsSsg.Src.Post.FilterConfigurationExtensions;
using static CsSsg.Src.Post.RoutingExtensions;
using CsSsg.Src.User;
using static CsSsg.Src.User.RoutingExtensions;

using CsSsg.Test.Db;
using CsSsg.Test.User;
using RepositoryExtensions = CsSsg.Src.Post.RepositoryExtensions;

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

    private async Task<(string, ClaimsPrincipal)> _nextUserAsync(AppDbContext continueContext, CancellationToken token)
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        var user = new Request(Email: $"{nextUserId}@test!post.filter", Password: $"test{nextUserId}");
        var (signupResult, signupClaims) = await DoPostUserSignupActionAsync(continueContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
        return (user.Email, signupClaims.ToIdentity());
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
        var cfConfig = ContentAccessFilterConfig;
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();
        var nullUser = AuthenticationExtensions.NullUser;

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.RequireInsertSuccess(_logger);
        
        _logger.LogInformation("Fetch permissions");
        var filter = new ContentAccessPermissionFilter(cfLogger, _cache, dbContext);
        var perms = await filter.GetPermissionsAsync(cfConfig, slug, user, token);
        perms.Match(
            p => Assert.Multiple(
                () => Assert.Equal(AccessLevel.FullControl, p.Item1),
                () => Assert.DoesNotContain(RepositoryExtensions.TAG_PUBLIC, p.Item2)),
            () => Assert.Fail("expected permissions but got none"));
        
        _logger.LogInformation("Fetch public permissions");
        var perms2 = await filter.GetPermissionsAsync(cfConfig, slug, nullUser, token);
        perms2.Match(p => Assert.Equal(AccessLevel.None, p.Item1),
            () => Assert.Fail("expected permissions but got none"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenMakeItPublic_ThenCheckPermissionsExistForAll()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var cfLogger = _loggerFactory.CreateLogger<ContentAccessPermissionFilter>();
        var (_, user) = await _nextUserAsync(dbContext, token);
        var uid = user.RequireUid();
        var nullUser = AuthenticationExtensions.NullUser;

        _logger.LogInformation("Create post");
        var post = new Contents($"Hello {_nextPostId}", "# World");
        var insertResult = await DoSubmitBlogEntryCreationAsync(post, uid, dbContext, _cache, rLogger, token);
        var slug = insertResult.RequireInsertSuccess(_logger);
        var cToken = new RepositoryExtensions.ConcurrencyToken();

        _logger.LogInformation("Set permissions");
        var newTags = new IManageCommand.PostTags(visibility: IManageCommand.PostVisibility.Public);
        var permsResult = await DoSubmitChangeTagsForNameAsync(slug, uid, 
            new IManageCommand.SetTags(newTags), cToken, dbContext, _cache, rLogger, token);
        permsResult.IfSome(failCode => Assert.Fail($"expected no error but got {failCode}"));
        
        _logger.LogInformation("Fetch permissions");
        var filter = new ContentAccessPermissionFilter(cfLogger, _cache, dbContext);
        var cfConfig = ContentAccessFilterConfig;
        var perms = await filter.GetPermissionsAsync(cfConfig, slug, user, token);
        perms.Match(
            p => Assert.Multiple(
                () =>  Assert.Equal(AccessLevel.FullControl, p.Item1),
                () => Assert.Contains(RepositoryExtensions.TAG_PUBLIC, p.Item2)),
            () => Assert.Fail("expected permissions but got none"));
        
        _logger.LogInformation("Fetch public permissions");
        var perms2 = await filter.GetPermissionsAsync(cfConfig, slug, nullUser, token);
        perms2.Match(
            p => Assert.Multiple(
                () =>  Assert.Equal(AccessLevel.Read, p.Item1),
                () => Assert.Contains(RepositoryExtensions.TAG_PUBLIC, p.Item2)),
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
            ((AccessLevel[])[AccessLevel.Write, AccessLevel.FullControl]).SelectMany(a => 
                    (bool[])[false, true],
                        // (b=anonymous|known) user attempts to edit post given (a=Wwrite|WritePublic) perms
                        (a, b) => (object?[])[a, b, null])
        );
        return l;
    }

    [Theory]
    [MemberData(nameof(TestDataForWritePermissionFilter))]
    public async Task TestDefaultWritePermissionFilter(object? oExistingAccessLevel, bool createUser, Type? expectedResult)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var user = AuthenticationExtensions.NullUser;
        if (createUser)
            user = (await _nextUserAsync(dbContext, token)).Item2;
        var wfLogger = _loggerFactory.CreateLogger<WritePermissionFilter>();
        var filter = new WritePermissionFilter(wfLogger, dbContext);
        var wfConfig = WriteFilterConfig;
        var existingAccessLevel = (AccessLevel?)oExistingAccessLevel;
        var result = await filter.VerifyPermissionAsync(wfConfig, existingAccessLevel, "unittest.", user, token);
        if (expectedResult is null)
            result.IfSome(r => Assert.Fail($"expected None but got {r}"));
        else
            result.Match(r => Assert.Equal(expectedResult, r.GetType()),
                () => Assert.Fail("expected {expectedResult} but got None"));
    }

    [InlineData(null, typeof(NotFound))]
    [InlineData(AccessLevel.Read, typeof(ForbidHttpResult))]
    [InlineData(AccessLevel.Write, null)]
    [Theory]
    public async Task TestForbidCreateWritePermissionFilter(object? oExistingAccessLevel, Type? expectedResult)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var user = (await _nextUserAsync(dbContext, token)).Item2;
        var wfLogger = _loggerFactory.CreateLogger<WritePermissionFilter>();
        var filter = new WritePermissionFilter(wfLogger, dbContext);
        var wfConfig = WriteFilterConfig 
            with { ForbidCreate = true };
        var existingAccessLevel = (AccessLevel?)oExistingAccessLevel;
        var result = await filter.VerifyPermissionAsync(wfConfig, existingAccessLevel, "unittest.", user, token);
        if (expectedResult is null)
            result.IfSome(r => Assert.Fail($"expected None but got {r}"));
        else
            result.Match(r => Assert.Equal(expectedResult, r.GetType()),
                () => Assert.Fail("expected {expectedResult} but got None"));
    }
    
    [InlineData(AccessLevel.Read, typeof(ForbidHttpResult))]
    [InlineData(AccessLevel.Write, typeof(ForbidHttpResult))]
    [InlineData(AccessLevel.FullControl, null)]
    [Theory]
    public async Task TestRestrictedWritePermissionFilter(object? oExistingAccessLevel, Type? expectedResult)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var user = (await _nextUserAsync(dbContext, token)).Item2;
        var wfLogger = _loggerFactory.CreateLogger<WritePermissionFilter>();
        var filter = new WritePermissionFilter(wfLogger, dbContext);
        var wfConfig = WriteFilterConfig 
            with { AllowedAccessLevelsForExistingContent = [AccessLevel.FullControl] };
        var existingAccessLevel = (AccessLevel?)oExistingAccessLevel;
        var result = await filter.VerifyPermissionAsync(wfConfig, existingAccessLevel, "unittest.", user, token);
        if (expectedResult is null)
            result.IfSome(r => Assert.Fail($"expected None but got {r}"));
        else
            result.Match(r => Assert.Equal(expectedResult, r.GetType()),
                () => Assert.Fail("expected {expectedResult} but got None"));
    }
#endregion
}