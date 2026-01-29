namespace CsSsg.Blog;

internal interface IContentSource
{
    Task<string?> GetContentOrNullAsync(string name, CancellationToken ct);
    Task SetContentAsync(string name, string content, CancellationToken ct);
    Task DeleteContentAsync(string name, CancellationToken ct);
}