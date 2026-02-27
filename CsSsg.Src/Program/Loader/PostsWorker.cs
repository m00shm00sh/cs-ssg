using System.Diagnostics.CodeAnalysis;
using CsSsg.Src.Blog;
using CsSsg.Src.Db;
using CsSsg.Src.Post;
using Contents = CsSsg.Src.Post.Contents;

namespace CsSsg.Src.Program.Loader;

internal class PostsWorkerConfig
{
    public required ILoggerFactory LoggerFactory;
    public required IHostEnvironment Environment;
    public required Func<AppDbContext> DbContextFactory;
    public required Guid UserId;
}

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
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

    private abstract class FileResult
    {
        // create a virtual function to force a custom stringer implementation for deriveds
        protected abstract string Line();
        public override string ToString() => Line();
    }

    private class SuccessResult(string slugName) : FileResult
    {
        protected override string Line() => $"Success: {slugName}";
    }

    private class ErrorResult(string failMessage) : FileResult
    {
        protected override string Line() => $"Error: {failMessage}";
    }

    private class SkippedResult : FileResult
    {
        public static SkippedResult _ = new();
        protected override string Line() => "Skipped";
    }

    private async Task<FileResult> _processFileAsync(string file, DateTime lastWriteUtc,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var contents = await File.ReadAllTextAsync(file, token);

        var h1 = MarkdownHandler.InferTitleOfMarkdownViaH1(contents);
        if (h1 is null)
            return new ErrorResult("could not infer title from first heading");

        await using var dbSession = _dbContextFactory();
        var data = new Contents(Title: h1, Body: contents);
        var slugName = data.ComputeSlugName();

        Failure? lastFailure = null;
        (await dbSession.UpdateContentIfOlderThanAsync(_userId, slugName, data, token, lastWriteUtc)).Match(
            (Failure f) => lastFailure = f,
            (bool didNotSkip) =>
            {
                if (!didNotSkip)
                    slugName = null;
            }
        );
        if (lastFailure is null)
        {
            if (slugName is null)
                return SkippedResult._;
            return new SuccessResult(slugName);
        }

        if (lastFailure != Failure.NotFound)
            return new ErrorResult($"Update failed: {lastFailure}");

        lastFailure = null;
        (await dbSession.CreateContentAsync(_userId, data, token)).Match(
            (Failure f) => lastFailure = f,
            (string success) => slugName = success
        );
        if (lastFailure is not null)
            return new ErrorResult($"Insert failed: {lastFailure}");
        
        (await dbSession.UpdatePermissionsAsync(_userId, slugName, true, token)).IfSome(
            (Failure f) => lastFailure = f
        );
        
        if (lastFailure is not null)
            return new ErrorResult($"Permissions fix failed: {lastFailure}");

        return new SuccessResult(slugName);
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
            LogFileResult(absFile, result);
        }
        LogFinishedDir(path);
    }

    [LoggerMessage(LogLevel.Information, "entering dir: {directory}")]
    partial void LogEnteringDir(string directory);

    [LoggerMessage(LogLevel.Information, "{nDirs} subdirs; {nFiles} files")]
    partial void LogNSubdirsNFiles(int nDirs, int nFiles);

    [LoggerMessage(LogLevel.Information, "{file}: {result}")]
    partial void LogFileResult(string file, FileResult result);

    [LoggerMessage(LogLevel.Information, "finished dir: {directory}")]
    partial void LogFinishedDir(string directory);
}