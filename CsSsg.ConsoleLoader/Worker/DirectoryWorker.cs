using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;

namespace CsSsg.ConsoleLoader.Worker;

internal partial class DirectoryWorker(
    ILoggerFactory loggerFactory, Func<string, bool> nameFilter, FileWorker worker) 
{
    private readonly ILogger<DirectoryWorker> _logger =  loggerFactory.CreateLogger<DirectoryWorker>();
    private readonly IFileProvider _currentDirFileProvider = new PhysicalFileProvider(Directory.GetCurrentDirectory());
    
    public DirectoryWorker(ILoggerFactory loggerFactory, string? nameFilter, FileWorker worker)
        : this(loggerFactory, _constructNameFilter(nameFilter), worker)
    { }

    private static Func<string, bool> _constructNameFilter(string? nameFilter)
    {
        if (string.IsNullOrWhiteSpace(nameFilter))
            return _ => true;
        var components = nameFilter.Split(':', 2);
        if (components.Length != 2)
            throw new ArgumentException($"invalid filter: {nameFilter}");
        var where = components[0];
        var what = components[1];
        return where switch
        {
            "start" => s => s.StartsWith(what),
            "end" => s => s.EndsWith(what),
            _ => throw new ArgumentException($"invalid filter: {where}")
        };
    }

    public async Task DoDirectoryAsync(string path, CancellationToken token)
    {
        LogEnteringDir(path);
        
        var dirs = new List<string>();
        var files = new List<(string, DateTime)>();

       
        foreach (var entry in _currentDirFileProvider.GetDirectoryContents(path))
        {
            if (entry.IsDirectory)
                dirs.Add(entry.Name);
            else
            {
                if (nameFilter(entry.Name))
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
            var result = await worker.HandleFileAsync(absFile, mtime, token);
            if (!result)
                break;
        }
        LogFinishedDir(path);
    }
    
    [LoggerMessage(LogLevel.Information, "entering dir: {directory}")]
    partial void LogEnteringDir(string directory);

    [LoggerMessage(LogLevel.Information, "{nDirs} subdirs; {nFiles} files")]
    partial void LogNSubdirsNFiles(int nDirs, int nFiles);

    [LoggerMessage(LogLevel.Information, "finished dir: {directory}")]
    partial void LogFinishedDir(string directory);
}