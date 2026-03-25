using KotlinScopeFunctions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.Db;
using CsSsg.Src.User;
using static CsSsg.Src.User.RoutingExtensions;

using CsSsg.Test.Db;

namespace CsSsg.Test.User;

public class ApiTests : IClassFixture<PostgresFixture>
{
#region scaffolding
    private readonly Func<AppDbContext> _contextFactory;
    private readonly ILogger<ApiTests> _logger;
    // this must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;

    public ApiTests(PostgresFixture fixture, ITestOutputHelper outputHelper)
    {
        _contextFactory = () => new AppDbContext(fixture.DbContextOptions);
        _logger = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper)).CreateLogger<ApiTests>();
    }
    
    private static int _nextUserId =>  Interlocked.Increment(ref _userCounter);
    
    private Request _nextDetails()
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        var user = new Request(Email: $"{nextUserId}@test!user", Password: $"test{nextUserId}");
        return user;
    }
#endregion
#region Signup and login
    [Fact]
    public async Task TestUserSignupThenLoginFlow()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = _nextDetails();
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
        var user = _nextDetails();
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
    public async Task TestUserLogin_ForbidsFailedLogin_MissingUser()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var user = _nextDetails();

        _logger.LogInformation("Login user");
        var (loginResult, _) = await DoPostUserLoginActionAsync(dbContext, user, token);
        Assert.NotNull(loginResult as ForbidHttpResult);
    }
    
    [Fact]
    public async Task TestUserSignupThenLogin_ForbidsFailedLogin_BadPassword()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var (signupResult, _) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);

        _logger.LogInformation("Login user");
        var (loginResult, _) = await DoPostUserLoginActionAsync(
            dbContext, user with { Password = "test04b" }, token);
        Assert.NotNull(loginResult as ForbidHttpResult);
    }
#endregion
#region user details
    [Fact]
    public async Task TestUserSignupThenLoginThenModifyFlow()
    {
        var utcNow = DateTime.UtcNow;
        
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = _nextDetails();
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

    [Fact]
    public async Task TestSetUserDetails_ForbidsTooLongEmail()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var uid = Guid.Empty;
        var user = new Request(Email: new string('b', 260), Password: "test06");
        _logger.LogInformation("Modify user");
        var detailsResult = await DoPostUserModifyActionAsync(uid, user, dbContext, token);
        var exp400 = detailsResult.Result as BadRequest;
        Assert.NotNull(exp400);
    }  
    
    [Fact]
    public async Task TestSetUserDetails_ForbidsInvalidUid()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var uid = Guid.Empty;
        _logger.LogInformation("Modify user");
        var user = _nextDetails();
        var detailsResult = await DoPostUserModifyActionAsync(uid, user, dbContext, token);
        var exp400 = detailsResult.Result as BadRequest;
        Assert.NotNull(exp400);
    }
#endregion
#region Create new post permissions
    [Fact]
    public async Task TestCreatedUser_ShouldBeAbleToCreateNewPost()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
            
        _logger.LogInformation("Check perms");
        var perms = await dbContext.DoesUserHaveCreatePermissionAsync(signupUid, token);
        Assert.True(perms);
    }
    
    [Fact]
    public async Task TestEmptyUser_ShouldNotBeAbleToCreateNewPost()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var perms = await dbContext.DoesUserHaveCreatePermissionAsync(Guid.Empty, token);
        Assert.False(perms);
    }
#endregion
#region Delete user
    [Fact]
    public async Task TestUserSignup_ThenDeletionFlow()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
            
        var deleteResult = await DoDeleteUserAsync(signupUid, user.Email, dbContext, token);
        Assert.NotNull(deleteResult as NoContent);
    }
    
    [Fact]
    public async Task TestUserDelete_FailsForMissingEmail()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var deleteResult = await DoDeleteUserAsync(Guid.Empty, "@/", dbContext, token);
        Assert.NotNull(deleteResult as NotFound);
    }
    
    [Fact]
    public async Task TestUserDelete_FailsForInvalidUid()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        _logger.LogInformation("Create user");
        var user = _nextDetails();
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(dbContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
            
        var deleteResult = await DoDeleteUserAsync(Guid.Empty, user.Email, dbContext, token);
        Assert.NotNull(deleteResult as ForbidHttpResult);
    }
    
#endregion
}