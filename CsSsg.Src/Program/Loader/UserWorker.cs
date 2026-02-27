using System.Diagnostics.CodeAnalysis;
using CsSsg.Src.Db;
using CsSsg.Src.User;

namespace CsSsg.Src.Program.Loader;

internal class UserWorkerConfig
{
    public required ILoggerFactory LoggerFactory;
    public required Func<AppDbContext> DbContextFactory;
}

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
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
        (await dbSession.LoginUserAsync(userDetails, token)).Match(
            (Failure f) =>
            {
                if (f != Failure.NotFound)
                    throw new InvalidOperationException($"could not login user {userDetails.Email}: {f}");
            },
            (Guid uid) => userId = uid
        );
        if (userId != Guid.Empty)
        {
            LogLoginSuccess();
            return userId;
        }

        LogNoSuchUserRegistering();

        (await dbSession.CreateUserAsync(userDetails, token)).Match(
            (Failure f) => throw new ArgumentException($"could not register user {userDetails.Email}: {f}"),
            (Guid uid) => {userId = uid; }
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
