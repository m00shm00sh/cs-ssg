
using CsSsg.Db;
using CsSsg.Program.Loader;
using CsSsg.User;
using Microsoft.EntityFrameworkCore;

namespace CsSsg.Program;

internal static class DirLoaderProgram
{
    public static void Run(string[] args)
    {
        CancellationTokenSource canceller = new();
        Console.CancelKeyPress += (_, e) =>
        {
            canceller.Cancel();
            e.Cancel = true;
        };
        RunAsync(args, canceller.Token).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string[] args, CancellationToken token)
    {
        var environment = ConsoleAppExtensions.EnvironmentWithLoggerFactory.Value;
        var config = ConsoleAppExtensions.Configuration.Value;

        var dbConOptionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        dbConOptionsBuilder.UseNpgsql(config.GetFromEnvironmentOrConfig("DB_URL", "ConnectionStrings:DbUrl"));
        AppDbContext DbContextFactory() => new(dbConOptionsBuilder.Options);

        var login = new Request
        {
            Email = config.GetFromEnvironmentOrConfig("LOADER_EMAIL", "Loader:Email"),
            Password = config.GetFromEnvironmentOrConfig("LOADER_PASS", "Loader:Password")
        };
        
        var userWorker = UserWorker.FromConfig(new UserWorkerConfig
        {
            LoggerFactory = environment.LoggerFactory,
            DbContextFactory = DbContextFactory
        });
        
        var userId = await userWorker.LoginOrRegisterUserAsync(login, token);

        var postWorker = PostsWorker.FromConfig(new PostsWorkerConfig
        {
            LoggerFactory = environment.LoggerFactory,
            Environment = environment,
            DbContextFactory = DbContextFactory,
            UserId = userId
        });
        await postWorker.DoDirectoryAsync("content", token);
    }
}