using CsSsg.Blog;
using CsSsg.Db;
using CsSsg.Post;
using Microsoft.AspNetCore.Routing.Constraints;
using OneOf;

namespace CsSsg.Program.Loader;

internal class PostsWorkerConfig
{
    public required ILoggerFactory LoggerFactory;
    public required IHostEnvironment Environment;
    public required Func<AppDbContext> DbContextFactory;
    public required Guid UserId;
}

internal partial class PostsWorker {
    internal static PostsWorker FromConfig(PostsWorkerConfig config)
    {
        var logger = config.LoggerFactory.CreateLogger<PostsWorker>();
        return new PostsWorker(config.UserId, logger, config.Environment, config.DbContextFactory);
    }
    
    private readonly Guid _userId;
    private readonly ILogger<PostsWorker> _logger;
    private readonly IHostEnvironment _environment;
    private readonly Func<AppDbContext> _dbContextFactory;

    private PostsWorker(Guid userId, ILogger<PostsWorker> logger, IHostEnvironment environment,
        Func<AppDbContext> dbContextFactory)
    {
        _userId = userId;
        _logger = logger;
        _environment = environment;
        _dbContextFactory = dbContextFactory;
    }

    private readonly record struct Success(string Value);

    private readonly record struct Error(string Value);

    private readonly record struct Skipped;

    private async Task<OneOf<Success, Skipped, Error>> _processFileAsync(string file, DateTime lastWriteUtc,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var contents = await File.ReadAllTextAsync(file, token);

        var h1 = MarkdownHandler.InferTitleOfMarkdownViaH1(contents);
        if (h1 is null)
            return new Error("could not infer title from first heading");

        await using var dbSession = _dbContextFactory();
        var data = new Contents(Title: h1, Body: contents);
        var slugName = data.ComputeSlugName();

        Failure? lastFailure = null;
        (await dbSession.UpdateContentIfOlderThanAsync(_userId, slugName, data, token, lastWriteUtc)).Switch(
            (bool didNotSkip) =>
            {
                if (!didNotSkip)
                    slugName = null;
            },
            (Failure f) => lastFailure = f
        );
        if (lastFailure is null)
        {
            if (slugName is null)
                return new Skipped();
            return new Success(slugName);
        }

        if (lastFailure != Failure.NotFound)
            return new Error($"Update failed: {lastFailure}");

        lastFailure = null;
        (await dbSession.CreateContentAsync(_userId, data, token)).Switch(
            (string success) => slugName = success,
            (Failure f) => lastFailure = f
        );
        if (lastFailure is not null)
            return new Error($"Insert failed: {lastFailure}");
        
        (await dbSession.UpdatePermissionsAsync(_userId, slugName, true, token)).Switch(
            /* (Success success) */ null,
            (Failure f) => lastFailure = f
        );
        
        if (lastFailure is not null)
            return new Error($"Permissions fix failed: {lastFailure}");
        
        return new Success(slugName);
    }

    public async Task DoDirectoryAsync(string path, CancellationToken token)
    {
        LogEnteringDir(path);
        
        var dirs = new List<string>();
        var files = new List<(string, DateTime)>();
        foreach (var entry in _environment.ContentRootFileProvider.GetDirectoryContents(path))
        {
            if (entry.IsDirectory)
                dirs.Add(entry.Name);
            else
            {
                if (entry.Name.EndsWith(".md"))
                    files.Add((entry.Name, entry.LastModified.UtcDateTime));
            }
        }
        LogNSubdirsNFiles(dirs.Count, files.Count);
        token.ThrowIfCancellationRequested();
        
        foreach (var dir in dirs)
            await DoDirectoryAsync(path + "/" + dir, token);
        foreach (var (file, mtime) in files)
        {
            var absFile = Path.Combine(path, file);
            var result = await _processFileAsync(absFile, mtime, token);
            result.Switch(
                (Success slug) => LogFileSuccessSlug(absFile, slug.Value),
                (Skipped _) => LogFileSkipped(absFile),
                (Error e) => LogFileFailedFailure(absFile, e.Value)
            );
        }
        LogFinishedDir(path);
    }

    [LoggerMessage(LogLevel.Information, "entering dir: {directory}")]
    partial void LogEnteringDir(string directory);

    [LoggerMessage(LogLevel.Information, "{nDirs} subdirs; {nFiles} files")]
    partial void LogNSubdirsNFiles(int nDirs, int nFiles);

    [LoggerMessage(LogLevel.Information, "{file}: Success: {slug}")]
    partial void LogFileSuccessSlug(string file, string slug);

    [LoggerMessage(LogLevel.Information, "{file}: Skipped")]
    partial void LogFileSkipped(string file);

    [LoggerMessage(LogLevel.Information, "{file}: Failed: {failure}")]
    partial void LogFileFailedFailure(string file, string failure);

    [LoggerMessage(LogLevel.Information, "finished dir: {directory}")]
    partial void LogFinishedDir(string directory);
}