using CsSsg.Src.User;

using CsSsg.ConsoleLoader.Worker;

namespace CsSsg.ConsoleLoader;

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

        var login = new Request
        {
            Email = config.GetFromEnvironmentOrConfig("LOADER_EMAIL", "Loader:Email"),
            Password = config.GetFromEnvironmentOrConfig("LOADER_PASS", "Loader:Password")
        };

        var client = new Client(environment.LoggerFactory, login, "http://localhost:8888");

        var postWorker = new PostsWorker(environment.LoggerFactory, environment, client);
        await postWorker.DoDirectoryAsync("content", token);
    }
}