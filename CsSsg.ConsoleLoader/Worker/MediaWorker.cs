using Microsoft.Extensions.Logging;
using HeyRed.Mime;
using LanguageExt;

using Entry = CsSsg.Src.Media.Entry;
using MObject = CsSsg.Src.Media.Object;

namespace CsSsg.ConsoleLoader.Worker;

internal class MediaWorker(ILoggerFactory loggerFactory) : IEntryWorker
{

    private record FileData(MObject Content, string Filename);
    
    public async Task<Either<FileWorker.ErrorResult, IEntryWorker.BoxedObject>> PrepareEntryFromFileAsync(
        string file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var stream = File.OpenRead(file);
        var filename = Path.GetFileName(file);
        var cType = MimeTypesMap.GetMimeType(filename);
        
        if (cType is null)
            return new FileWorker.ErrorResult($"could not infer type for {filename}");
        
        return new IEntryWorker.BoxedObject(new FileData(new MObject(cType, stream), filename));
    }

    public Task<FileWorker.FileResult> TryUpdateAsync(object entry, Client client, CancellationToken token)
        => FileWorker.TryRequest(async () =>
        {
            var data = (FileData)entry;
            var slugName = Entry.SlugifyFilename(data.Filename);
            await client.PutFileNoResponseAsync($"/api/v1/media/{slugName}", data.Content, token);
            return slugName;
        });

    public Task<FileWorker.FileResult> TryCreateAsync(object entry, Client client, CancellationToken token)
        => FileWorker.TryRequest(async () =>
        {
            var data = (FileData)entry;
            var result = await client.PostFileAsync<string>("/api/v1/media", data.Content, data.Filename, token);
            return result!;
        });
    
    public string PermissionsLink(string slug)
        => $"/api/v1/media/{slug}/permissions";
}