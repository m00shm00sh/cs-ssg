using CsSsg.Db;
using CsSsg.User;

namespace CsSsg.Program.Loader;

internal class UserWorkerConfig
{
    public required ILoggerFactory LoggerFactory;
    public required Func<AppDbContext> DbContextFactory;
}

internal partial class UserWorker
{
    internal static UserWorker FromConfig(UserWorkerConfig config)
    {
        var logger = config.LoggerFactory.CreateLogger<UserWorker>();
        return new UserWorker(logger, config.DbContextFactory);
    }

    private readonly ILogger<UserWorker> _logger;
    private readonly Func<AppDbContext> _dbContextFactory;
    
    private UserWorker(ILogger<UserWorker> logger, Func<AppDbContext> dbContextFactory)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    public async Task<Guid> LoginOrRegisterUserAsync(Request userDetails, CancellationToken token)
    {
        await using var dbSession = _dbContextFactory();
        var userId = Guid.Empty;
        
        LogBeginLoginUser(userDetails.Email);
        (await dbSession.LoginUserAsync(userDetails, token)).Switch(
            (Guid uid) => userId = uid,
            (Failure f) =>
            {
                if (f != Failure.NotFound)
                    throw new InvalidOperationException($"could not login user {userDetails.Email}: {f}");
            }
        );
        if (userId != Guid.Empty)
        {
            LogLoginSuccess();
            return userId;
        }
        LogNoSuchUserRegistering();

        (await dbSession.CreateUserAsync(userDetails, token)).Switch(
            (Guid uid) => userId = uid,
            (Failure f) => throw new ArgumentException($"could not register user {userDetails.Email}: {f}")
        );
        LogRegisterSuccess();
        
        return userId;
    }

    [LoggerMessage(LogLevel.Information, "Trying to login user {user}")]
    partial void LogBeginLoginUser(string user);

    [LoggerMessage(LogLevel.Information, "login success")]
    partial void LogLoginSuccess();

    [LoggerMessage(LogLevel.Information, "no such user; registering")]
    partial void LogNoSuchUserRegistering();

    [LoggerMessage(LogLevel.Information, "register success")]
    partial void LogRegisterSuccess();
}
