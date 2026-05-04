using Microsoft.Extensions.Logging;
using LanguageExt;

using CsSsg.Src.Post;

namespace CsSsg.ConsoleLoader.Worker;

internal class PostWorker(ILoggerFactory loggerFactory) : IEntryWorker {
    
    public async Task<Either<FileWorker.ErrorResult, IEntryWorker.BoxedObject>> PrepareEntryFromFileAsync(
        string file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        string contents;
        try
        {
            contents = await File.ReadAllTextAsync(file, token);
        }
        catch (Exception e)
        {
            return new FileWorker.ErrorResult($"read failed: {e.Message}");
        }

        var h1 = MarkdownHandler.InferTitleOfMarkdownViaH1(contents);
        if (h1 is null)
            return new FileWorker.ErrorResult("could not infer title from first heading");

        var request = new Contents
        {
            Title = h1,
            Body = contents
        };
        return new IEntryWorker.BoxedObject(request);
    }

    public Task<FileWorker.FileResult> TryUpdateAsync(object entry, Client client, CancellationToken token)
        => FileWorker.TryRequest(async () =>
        {
            var contents = (Contents)entry;
            var slugName = contents.ComputeSlugName();
            await client.PutJsonNoResponseAsync($"/api/v1/blog/{slugName}", contents, token);
            return slugName;
        });

    public Task<FileWorker.FileResult> TryCreateAsync(object entry, Client client, CancellationToken token)
        => FileWorker.TryRequest(async () =>
        {
            var contents = (Contents)entry;

            var result = await client.PostJsonAsync<Contents, string>("/api/v1/blog", contents, token);
            return result!;
        });
    
    public string PermissionsLink(string slug)
        => $"/api/v1/blog/{slug}/permissions";
}