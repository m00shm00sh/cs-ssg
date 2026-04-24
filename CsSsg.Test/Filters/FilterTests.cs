using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Filters;
using CsSsg.Test.SharedTypes;

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
    [Fact]
    public async Task TestContentAccessFilter_UsesConfiguratorCallback()
    {
        var token = CancellationToken.None;
        var cfLogger = _loggerFactory.CreateLogger<ContentAccessPermissionFilter>();
        var accessLevel = RefBox.Create((AccessLevel?)null);
        // ReSharper disable once ConvertToLocalFunction
        ContentAccessPermissionFilterConfigurator.GetPermissionsFromDatabaseAsync callback = (_, _, _, _) =>
            new ValueTask<AccessLevel?>(accessLevel.Value);
        var cfConfig =
            new ContentAccessPermissionFilterConfigurator("unittest.filter", callback);

        _logger.LogInformation("Query filter");
        var filter = new ContentAccessPermissionFilter(cfLogger, _cache, null!);
        var perms = await filter.GetPermissionsAsync(cfConfig, "a", Guid.Empty, token);
        perms.IfSome(p => Assert.Fail($"expected no access"));
        accessLevel.Value = AccessLevel.Read;
        perms = await filter.GetPermissionsAsync(cfConfig, "b", Guid.Empty, token);
        perms.Match(p => Assert.Equal(AccessLevel.Read, p),
            () => Assert.Fail("expected AccessLevel"));
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
            ((AccessLevel[])[AccessLevel.Write, AccessLevel.WritePublic]).SelectMany(a => 
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
        WritePermissionFilterConfigurator.DoesUserHaveCreatePermissionsFromDatabaseAsync callback =
            (_, _, _) => new ValueTask<bool>(hasCreatePerms.Value);
        var wfConfig = new WritePermissionFilterConfigurator("unittest.filter", callback);
        
        var existingAccessLevel = (AccessLevel?)oExistingAccessLevel;
        var result = await filter.VerifyPermissionAsync(wfConfig, existingAccessLevel,
            "unittest.", Guid.Empty, token);
        if (expectedResult is null)
            result.IfSome(r => Assert.Fail($"expected None but got {r}"));
        else
            result.Match(r => Assert.Equal(expectedResult, r.GetType()),
                () => Assert.Fail("expected {expectedResult} but got None"));
    }
#endregion
}