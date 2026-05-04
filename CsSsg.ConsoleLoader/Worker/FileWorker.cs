using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

using CsSsg.Src.Post;

namespace CsSsg.ConsoleLoader.Worker;

internal partial class FileWorker(ILoggerFactory loggerFactory, IEntryWorker worker, Client client) {
    private readonly ILogger<FileWorker> _logger =  loggerFactory.CreateLogger<FileWorker>();

    internal abstract class FileResult
    {
        public abstract string Line();
        public abstract int Offset { get; }

        public override string ToString() => Line();
    }

    internal class SuccessResult(string slugName) : FileResult
    {
        public override string Line() => $"Success: {slugName}";
        public override int Offset => "Success: ".Length;
    }

    internal class ErrorResult(string failMessage) : FileResult
    {
        public override string Line() => $"Error: {failMessage}";
        public override int Offset => "Error: ".Length;
    }

    private static readonly FrozenSet<int> _retryUpdateAsInsertCodes = new List<HttpStatusCode?>
    {
        HttpStatusCode.NotFound, HttpStatusCode.Forbidden
    }.Select(r => (int)r!).ToFrozenSet();

    public async Task<bool> HandleFileAsync(string file, DateTime lastWriteUtc, CancellationToken token)
    {
        LogProcessingFile(file, lastWriteUtc);
        var result = await _doHandleFileAsync(file, lastWriteUtc, token);
        LogFileResult(file, result);
        return result switch
        {
            SuccessResult => true,
            ErrorResult => false
        };
    }
    
    private async Task<FileResult> _doHandleFileAsync(string file, DateTime lastWriteUtc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        
        var entryResult = await worker.PrepareEntryFromFileAsync(file, token);
        if (entryResult.IsLeft)
            return (ErrorResult)entryResult;
        var entry = ((IEntryWorker.BoxedObject)entryResult).Obj;

        var updateResult = await worker.TryUpdateAsync(entry, client, token);
        string slug = updateResult.Line()[updateResult.Offset..];
        if (updateResult is not SuccessResult)
        {
            var msg = updateResult.Line()[updateResult.Offset..];
            if (!int.TryParse(msg[..3], out int httpCode) || !_retryUpdateAsInsertCodes.Contains(httpCode))
                return updateResult;
            var insertResult = await worker.TryCreateAsync(entry, client, token);
            if (insertResult is ErrorResult)
                return insertResult;
            slug = insertResult.Line()[insertResult.Offset..];
        }
            
        var newPerms = new IManageCommand.SetPermissions(new IManageCommand.Permissions
        {
            Public = true 
        });
        await client.PostJsonNoResponseAsync(worker.PermissionsLink(slug), newPerms, token);
        return new SuccessResult(slug);
    }

    public static async Task<FileResult> TryRequest(Func<Task<string>> requestWorker)
    {
        try
        {
            var result = await requestWorker();
            return new SuccessResult(result);
        }
        catch (HttpRequestException e)
        {
            var status = e.StatusCode;
            return new ErrorResult(status.HasValue
                ? $"{(int)status}: {e.Message}"
                : e.Message);
        }
    }

    [LoggerMessage(LogLevel.Information, "processing file: {fileName}; mtime={mTime}")]
    partial void LogProcessingFile(string fileName, DateTime mTime);
    
    [LoggerMessage(LogLevel.Information, "{file}: {result}")]
    partial void LogFileResult(string file, FileResult result);

}