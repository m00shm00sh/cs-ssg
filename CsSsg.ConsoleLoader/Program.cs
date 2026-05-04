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

foreach (var dir in config.Dir)
{
    switch (dir.Type)
    {
        case Type.Content:
            var contentWorker = new PostWorker(loggerFactory);
            var fileWorker = new FileWorker(loggerFactory, contentWorker, client);
            var dirWorker = new DirectoryWorker(loggerFactory, dir.NameFilter, fileWorker);
            await dirWorker.DoDirectoryAsync(dir.Path, canceller.Token);
            break;
        case Type.Media:
            throw new NotImplementedException("media worker not implemented");
        default:
            throw new ArgumentOutOfRangeException();
    }
}
