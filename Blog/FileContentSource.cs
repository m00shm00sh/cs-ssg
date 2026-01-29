namespace CsSsg.Blog;

internal class FileContentSource : IContentSource
{
    public async Task<string?> GetContentOrNullAsync(string name, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync($"content/blog/{name}.md", ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public Task SetContentAsync(string name, string content, CancellationToken ct)
        => File.WriteAllTextAsync($"content/blog/{name}.md", content, ct);

    public Task DeleteContentAsync(string name, CancellationToken ct)
        => Task.Run(() => File.Delete($"content/blog/{name}.md"), ct);
}