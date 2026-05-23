using System.Security.Claims;
using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
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