using CsSsg.Src.Db;
using CsSsg.Src.User;
using static CsSsg.Src.User.RoutingExtensions;

using CsSsg.Test.Db;
using KotlinScopeFunctions;
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
    public async Task TestUserSignup_ForbidsDoubleRegistration()
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
    
    [Fact]
    public async Task TestUserSignup_ForbidsTooLongEmail()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = new Request(Email: new string('a', 260), Password: "test02a");
        var (signupResult, _) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as BadRequest<Failure>);
    }

    [Fact]
    public async Task TestUserLogin_ForbidsFailedLogin()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var user = new Request(Email: "03@test!user", Password: "test03");

        _logger.LogInformation("Login user");
        var (loginResult, _) = await DoPostUserLoginActionAsync(dbContext, user, token);
        Assert.NotNull(loginResult as ForbidHttpResult);
    }
    
    [Fact]
    public async Task TestUserSignupThenLoginThenModifyFlow()
    {
        var utcNow = DateTime.UtcNow;
        
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = new Request(Email: "04@test!user", Password: "test04");
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);

        _logger.LogInformation("Login user");
        var (loginResult, loginUid) = await DoPostUserLoginActionAsync(dbContext, user, token);
        Assert.NotNull(loginResult as RedirectHttpResult);
        
        Assert.Equal(signupUid, loginUid);
        
        _logger.LogInformation("Get user details");
        var detailsResult = await DoGetUserModifyPageAsync(loginUid, dbContext, token);
        var detailsEntry = (detailsResult.Result as Ok<UserEntry>)?.Value;
        Assert.NotNull(detailsEntry);
        
        // compensate for system load by assuming that the database is within an hour of app
        var cTimeDiff = ((detailsEntry.Value.CreatedAt.DateTime - utcNow).TotalHours).Let(Math.Abs);
        Assert.InRange(cTimeDiff, 0, 1);
        var mTimeDiff = detailsEntry.Value.Let(d => d.UpdatedAt - d.CreatedAt);
        
        // we should only have nonzero time delta after *update* not *insert*
        Assert.Equal(0, mTimeDiff.TotalSeconds);
        
        _logger.LogInformation("Modify user");
        var modifyResult = await DoPostUserModifyActionAsync(
            loginUid, user with { Password = "test04a" }, dbContext, token);
        Assert.NotNull(modifyResult.Result as RedirectHttpResult);
    }

    [Fact]
    public async Task TestGetUserDetails_FailsForMissingUser()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;

        var invalidUser = Guid.Empty;

        var detailsResult = await DoGetUserModifyPageAsync(invalidUser, dbContext, token);
        var exp403 = detailsResult.Result as ForbidHttpResult;
        Assert.NotNull(exp403);
    }
    
}