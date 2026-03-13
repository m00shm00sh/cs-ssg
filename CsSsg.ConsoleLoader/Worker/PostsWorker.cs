using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CsSsg.Src.Post;

namespace CsSsg.ConsoleLoader.Worker;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
internal partial class PostsWorker(ILoggerFactory loggerFactory, IHostEnvironment environment, Client client) {
    private readonly ILogger<Client> _logger =  loggerFactory.CreateLogger<Client>();
    
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

    private async Task<FileResult> _processFileAsync(string file, DateTime lastWriteUtc,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var contents = await File.ReadAllTextAsync(file, token);
        
        var h1 = MarkdownHandler.InferTitleOfMarkdownViaH1(contents);
        if (h1 is null)
            return new ErrorResult("could not infer title from first heading");

        var request = new Contents
        {
            Title = h1,
            Body = contents
        };

        var slugName = request.ComputeSlugName();
        
        LogProcessingSlug(slugName);
        try
        {
            await client.PutJsonNoResponseAsync($"/api/v1/blog/{slugName}", request, token);
            return new SuccessResult(slugName);
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode != HttpStatusCode.NotFound)
                return new ErrorResult($"Update failed: {e.StatusCode}");
        }

        try
        {
            var inserted = await client.PostJsonAsync<Contents, string>("/api/v1/blog/-new", request, token);
            slugName = inserted!;
        }
        catch (HttpRequestException e)
        {
            return new ErrorResult($"Insert failed: {e.StatusCode}");
        }

        var newState = new ManageCommand
        {
            NewPermissions = new ManageCommand.Permissions { Public = true },
        };
        // let exception propagate because a failure to set to public is a backend bug
        await client.PostJsonNoResponseAsync($"/api/v1/blog/{slugName}/manage", newState, token);
        return new SuccessResult(slugName);
    }

    public async Task DoDirectoryAsync(string path, CancellationToken token)
    {
        LogEnteringDir(path);
        
        var dirs = new List<string>();
        var files = new List<(string, DateTime)>();
        foreach (var entry in environment.ContentRootFileProvider.GetDirectoryContents(path))
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

    [LoggerMessage(LogLevel.Information, "processing slug: {slugName}")]
    partial void LogProcessingSlug(string slugName);
    
    [LoggerMessage(LogLevel.Information, "entering dir: {directory}")]
    partial void LogEnteringDir(string directory);

    [LoggerMessage(LogLevel.Information, "{nDirs} subdirs; {nFiles} files")]
    partial void LogNSubdirsNFiles(int nDirs, int nFiles);

    [LoggerMessage(LogLevel.Information, "{file}: {result}")]
    partial void LogFileResult(string file, FileResult result);

    [LoggerMessage(LogLevel.Information, "finished dir: {directory}")]
    partial void LogFinishedDir(string directory);
}