using System.Security.Claims;
using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Filters;
using CsSsg.Src.Post;
using CsSsg.Test.SharedTypes;
using CsSsg.Test.User;

namespace CsSsg.Test.Filters;

public class FilterTests
{
#region scaffolding
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FilterTests> _logger;
    private readonly IFusionCache _cache = new FusionCache(new FusionCacheOptions());
    
    public FilterTests(ITestOutputHelper outputHelper)
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper));
        _logger = _loggerFactory.CreateLogger<FilterTests>();
    }
    
#endregion
#region ContentAccessPermissionFilter
    private static readonly Guid id1 = Guid.NewGuid();
    private static readonly Guid id2 = Guid.NewGuid();

    [InlineData("n", AccessLevel.None, 1, new string[]{})]
    [InlineData("r", AccessLevel.Read, 1, new[]{"b"})]
    [InlineData("w", AccessLevel.Write, 1, new[]{"c"})]
    [InlineData("f", AccessLevel.FullControl, 2, new string[]{})]
    [Theory]
    public async Task TestContentAccessFilter_UsesConfiguratorCallback(string slug, AccessLevel expAccess, int whichUid,
        string[] contentTags)
    {
        var token = CancellationToken.None;
        var cfLogger = _loggerFactory.CreateLogger<ContentAccessPermissionFilter>();
        var ownerId = whichUid switch
        {
            1 => id1,
            2 => id2,
            _ => throw new ArgumentOutOfRangeException(nameof(whichUid), whichUid, null)
        };

        var user = new Src.User.RepositoryExtensions.UserClaims(id2, [
            (RoleNamespace.Search, "a"),
            (RoleNamespace.View, "b"),
            (RoleNamespace.Edit, "c")
        ]).ToIdentity();
        
        // ReSharper disable once ConvertToLocalFunction
        ContentAccessPermissionFilterConfigurator.GetPermissionsFromDatabaseAsync callback = (_, _, _) =>
            new ValueTask<RepositoryExtensions.PostPermissions?>(
                new RepositoryExtensions.PostPermissions(ownerId, contentTags.ToList(), 
                    new RepositoryExtensions.ConcurrencyToken()));
        var cfConfig =
            new ContentAccessPermissionFilterConfigurator("unittest.filter", callback);

        _logger.LogInformation("Query filter");
        var filter = new ContentAccessPermissionFilter(cfLogger, _cache, null!);
        var perms = await filter.GetPermissionsAsync(cfConfig, slug, user, token);
        perms.IfNone(() => Assert.Fail("unexpected: did not get permissions tuple"));
        var (level, gotTags, _) = perms.ToNullable()!.Value;
        Assert.Multiple(
            () => Assert.Equal(expAccess, level),
            () => Assert.Equal(contentTags, gotTags)
        );
    }

    private static (DefaultHttpContext Http, TestEndpointFilterInvocationContext Context) _makeFilterContext(
        string method, string slug, ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        http.Request.Method = method;
        http.Request.RouteValues["name"] = slug;
        return (http, new TestEndpointFilterInvocationContext(http));
    }

    private (ContentAccessPermissionFilter Filter, ContentAccessPermissionFilterConfigurator Config, RefBox<int> Lookups)
        _makeConflictFilter(string category)
    {
        var lookups = RefBox.Create(0);
        var config = new ContentAccessPermissionFilterConfigurator(category, (_, _, _) =>
        {
            var lookup = ++lookups.Value;
            return new ValueTask<RepositoryExtensions.PostPermissions?>(
                new RepositoryExtensions.PostPermissions(id1, [], new RepositoryExtensions.ConcurrencyToken(lookup)));
        });
        var filter = new ContentAccessPermissionFilter(
            _loggerFactory.CreateLogger<ContentAccessPermissionFilter>(), _cache, null!);
        return (filter, config, lookups);
    }

    [Fact]
    public async Task TestContentAccessFilter_OnGetConflict_InvalidatesCacheAndRetriesOnce()
    {
        var slug = "unittest-conflict-get";
        var user = new Src.User.RepositoryExtensions.UserClaims(id1, []).ToIdentity();
        var (filter, config, lookups) = _makeConflictFilter("unittest.conflict.get");
        var (_, context) = _makeFilterContext(HttpMethods.Get, slug, user);
        await config.InvokeAsync(context, _ => ValueTask.FromResult<object?>(null));

        var seenTokens = new List<int>();
        var handlerCalls = 0;
        ValueTask<object?> Next(EndpointFilterInvocationContext c)
        {
            handlerCalls++;
            seenTokens.Add(c.HttpContext.RequireConcurrencyToken().Value);
            return ValueTask.FromResult<object?>(TypedResults.Conflict());
        }

        var result = await filter.InvokeAsync(context, Next);

        Assert.Multiple(
            () => Assert.Equal(2, handlerCalls), // retried exactly once
            () => Assert.Equal([1, 2], seenTokens), // the retry used refreshed permissions
            () => Assert.Equal(2, lookups.Value), // the stale access cache entry was dropped
            () => Assert.IsType<Conflict>(result)
        );
    }

    [Fact]
    public async Task TestContentAccessFilter_OnGetConflictWrappedInTypedResults_StillRetries()
    {
        var slug = "unittest-conflict-nested";
        var user = new Src.User.RepositoryExtensions.UserClaims(id1, []).ToIdentity();
        var (filter, config, lookups) = _makeConflictFilter("unittest.conflict.nested");
        var (_, context) = _makeFilterContext(HttpMethods.Get, slug, user);
        await config.InvokeAsync(context, _ => ValueTask.FromResult<object?>(null));

        var handlerCalls = 0;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            handlerCalls++;
            // route handlers declare Results<...> return types, which wrap the actual IResult
            Results<Ok, Conflict> nested = TypedResults.Conflict();
            return ValueTask.FromResult<object?>(nested);
        }

        var result = await filter.InvokeAsync(context, Next);

        Assert.Multiple(
            () => Assert.Equal(2, handlerCalls),
            () => Assert.Equal(2, lookups.Value),
            () => Assert.IsType<Results<Ok, Conflict>>(result)
        );
    }

    [Fact]
    public async Task TestContentAccessFilter_OnGetConflictWrappedInPhase1TypedUnions_StillRetries()
    {
        // these are the unions returned by the typed stats and media fetch handlers
        Results<Ok<CsSsg.Src.Media.Stats>, NotFound, Conflict> stats = TypedResults.Conflict();
        await _assertTypedConflictRetriedOnceAsync("unittest.conflict.stats", stats);

        Results<FileStreamHttpResult, ForbidHttpResult, NotFound, Conflict> fetch = TypedResults.Conflict();
        await _assertTypedConflictRetriedOnceAsync("unittest.conflict.fetch", fetch);
    }

    private async Task _assertTypedConflictRetriedOnceAsync<TResults>(string category, TResults conflictResult)
        where TResults : IResult
    {
        var slug = $"unittest-{category}";
        var user = new Src.User.RepositoryExtensions.UserClaims(id1, []).ToIdentity();
        var (filter, config, lookups) = _makeConflictFilter(category);
        var (_, context) = _makeFilterContext(HttpMethods.Get, slug, user);
        await config.InvokeAsync(context, _ => ValueTask.FromResult<object?>(null));

        var handlerCalls = 0;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            handlerCalls++;
            return ValueTask.FromResult<object?>(conflictResult);
        }

        var result = await filter.InvokeAsync(context, Next);

        Assert.Multiple(
            () => Assert.Equal(2, handlerCalls), // retried exactly once
            () => Assert.Equal(2, lookups.Value), // the stale access cache entry was dropped
            () => Assert.IsType<TResults>(result),
            () => Assert.IsType<Conflict>(((INestedHttpResult)result!).Result)
        );
    }

    [Fact]
    public async Task TestContentAccessFilter_OnMutationConflict_InvalidatesCacheWithoutRetry()
    {
        var slug = "unittest-conflict-post";
        var user = new Src.User.RepositoryExtensions.UserClaims(id1, []).ToIdentity();
        var (filter, config, lookups) = _makeConflictFilter("unittest.conflict.post");
        var (_, context) = _makeFilterContext(HttpMethods.Post, slug, user);
        await config.InvokeAsync(context, _ => ValueTask.FromResult<object?>(null));

        var handlerCalls = 0;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            handlerCalls++;
            return ValueTask.FromResult<object?>(TypedResults.Conflict());
        }

        var result = await filter.InvokeAsync(context, Next);

        Assert.Multiple(
            () => Assert.Equal(1, handlerCalls), // mutations must not be replayed
            () => Assert.Equal(1, lookups.Value),
            () => Assert.IsType<Conflict>(result)
        );
        var cached = await _cache.TryGetAsync<RepositoryExtensions.PostPermissions?>(
            $"access-unittest.conflict.post/{slug}", token: TestContext.Current.CancellationToken);
        Assert.False(cached.HasValue); // the stale entry was removed
    }

    [Fact]
    public async Task TestContentAccessFilter_OnSuccess_DoesNotInvalidateCache()
    {
        var slug = "unittest-success";
        var user = new Src.User.RepositoryExtensions.UserClaims(id1, []).ToIdentity();
        var (filter, config, lookups) = _makeConflictFilter("unittest.success");
        var (_, context) = _makeFilterContext(HttpMethods.Get, slug, user);
        await config.InvokeAsync(context, _ => ValueTask.FromResult<object?>(null));

        var handlerCalls = 0;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            handlerCalls++;
            return ValueTask.FromResult<object?>(TypedResults.Ok());
        }

        var result = await filter.InvokeAsync(context, Next);

        Assert.Multiple(
            () => Assert.Equal(1, handlerCalls),
            () => Assert.Equal(1, lookups.Value),
            () => Assert.IsType<Ok>(result)
        );
        var cached = await _cache.TryGetAsync<RepositoryExtensions.PostPermissions?>(
            $"access-unittest.success/{slug}", token: TestContext.Current.CancellationToken);
        Assert.True(cached.HasValue);
        Assert.Equal(1, cached.Value!.ConcurrencyToken.Value);
    }
#endregion
#region WritePermissionFilter
    public static IList<object?[]> TestDataForWritePermissionFilter()
    {
        List<object?[]> l = 
        [ // [ AccessLevel? existingAccess, bool createUser, Type<out IResult>? ExpectedResult ]
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
    public async Task TestWritePermissionFilter_UsesConfiguratorCallback(object? oExistingAccessLevel, bool createUser,
        Type? expectedResult)
    {
        var token = CancellationToken.None;
        var wfLogger = _loggerFactory.CreateLogger<WritePermissionFilter>();
        var filter = new WritePermissionFilter(wfLogger, null!);
        
        var hasCreatePerms = RefBox.Create(createUser);
        
        // ReSharper disable once ConvertToLocalFunction
        WritePermissionFilterConfigurator.DoesUserHaveCreatePermissionsFromClaimsAsync callback =
            (_, _, _) => new ValueTask<bool>(hasCreatePerms.Value);
        var wfConfig = new WritePermissionFilterConfigurator("unittest.filter", callback);
        
        var existingAccessLevel = (AccessLevel?)oExistingAccessLevel;
        var result = await filter.VerifyPermissionAsync(wfConfig, existingAccessLevel,
            "unittest.", AuthenticationExtensions.NullUser, token);
        if (expectedResult is null)
            result.IfSome(r => Assert.Fail($"expected None but got {r}"));
        else
            result.Match(r => Assert.Equal(expectedResult, r.GetType()),
                () => Assert.Fail("expected {expectedResult} but got None"));
    }
#endregion
}