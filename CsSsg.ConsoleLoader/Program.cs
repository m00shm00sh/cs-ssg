using CsSsg.ConsoleLoader;
using CsSsg.ConsoleLoader.Worker;
using Type = CsSsg.ConsoleLoader.Type;

CancellationTokenSource canceller = new();
Console.CancelKeyPress += (_, e) =>
{
    canceller.Cancel();
    e.Cancel = true;
};

var (config, loggerFactory) = Config.Parse("console-loader.toml");

var client = new Client(loggerFactory, config.Login, config.Server);

var postWorker = new PostsWorker(loggerFactory, client);

foreach (var dir in config.Dir)
{
    switch (dir.Type)
    {
        case Type.Content:
            await postWorker.DoDirectoryAsync(dir.Path, canceller.Token);
            break;
        case Type.Media:
            throw new NotImplementedException("media worker not implemented");
        default:
            throw new ArgumentOutOfRangeException();
    }
}
