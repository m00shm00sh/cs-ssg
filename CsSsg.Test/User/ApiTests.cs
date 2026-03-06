using CsSsg.Src.Db;
using CsSsg.Src.Slices.ViewModels;
using CsSsg.Src.User;
using static CsSsg.Src.User.RoutingExtensions;

using CsSsg.Test.Db;
using MartinCostello.Logging.XUnit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CsSsg.Test.User;

public class ApiTests : IClassFixture<PostgresFixture>
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly ILogger<ApiTests> _logger;

    public ApiTests(PostgresFixture fixture, ITestOutputHelper outputHelper)
    {
        _contextFactory = () => new AppDbContext(fixture.DbContextOptions);
        _logger = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper)).CreateLogger<ApiTests>();
    }
    
    [Fact]
    public async Task TestUserSignupThenLoginFlow()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = new Request(Email: "01@test!user", Password: "test01");
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);

        _logger.LogInformation("Login user");
        var (loginResult, loginUid) = await DoPostUserLoginActionAsync(dbContext, user, token);
        Assert.NotNull(loginResult as RedirectHttpResult);
        
        Assert.Equal(signupUid, loginUid);
    }
    
    [Fact]
    public async Task TestUserSignupForbidsDoubleRegistration()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = new Request(Email: "02@test!user", Password: "test02");
        var (signupResult, _) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
        
        _logger.LogInformation("Attempt to create duplicate user");
        var (signupResult2, _) = await DoPostUserSignupActionAsync(dbContext, user, token);
        var signupResult2BrF = signupResult2 as BadRequest<Failure>;
        Assert.Equal(Failure.Conflict, signupResult2BrF?.Value);
    }
}