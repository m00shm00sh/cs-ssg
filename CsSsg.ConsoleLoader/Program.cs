using CsSsg.Src.User;

using CsSsg.ConsoleLoader;
using CsSsg.ConsoleLoader.Worker;

CancellationTokenSource canceller = new();
Console.CancelKeyPress += (_, e) =>
{
    canceller.Cancel();
    e.Cancel = true;
};

var environment = ConsoleAppExtensions.EnvironmentWithLoggerFactory.Value;
var config = ConsoleAppExtensions.Configuration.Value;

var login = new Request
{
    Email = config.GetFromEnvironmentOrConfig("LOADER_EMAIL", "Loader:Email"),
    Password = config.GetFromEnvironmentOrConfig("LOADER_PASS", "Loader:Password")
};

var client = new Client(environment.LoggerFactory, login, "http://localhost:8888");

var postWorker = new PostsWorker(environment.LoggerFactory, environment, client);
await postWorker.DoDirectoryAsync("content", canceller.Token);
