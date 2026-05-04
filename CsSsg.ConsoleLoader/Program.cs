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

var dirHandlers = new Dictionary<Type, Func<DirCommand, Task>>
{
    [Type.Content] = async dir =>
    {
        var contentWorker = new PostWorker(loggerFactory);
        var fileWorker = new FileWorker(loggerFactory, contentWorker, client);
        var dirWorker = new DirectoryWorker(loggerFactory, dir.NameFilter, fileWorker);
        await dirWorker.DoDirectoryAsync(dir.Path, canceller.Token);
    },
    [Type.Media] = async dir =>
    {
        var contentWorker = new MediaWorker(loggerFactory);
        var fileWorker = new FileWorker(loggerFactory, contentWorker, client);
        var dirWorker = new DirectoryWorker(loggerFactory, dir.NameFilter, fileWorker);
        await dirWorker.DoDirectoryAsync(dir.Path, canceller.Token);
    },
};

foreach (var dir in config.Dir)
{
    if (dirHandlers.TryGetValue(dir.Type, out var handler))
        await handler(dir);
    else
        throw new NotSupportedException($"unhandled dir type: {dir.Type}");
}
