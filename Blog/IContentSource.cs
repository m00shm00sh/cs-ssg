using OneOf;
using OneOf.Types;

namespace CsSsg.Blog;

public interface IContentSource
{
    public record struct Entry(string Title, string Name, DateTime LastModified);

    public record struct Contents(string? Title, string Body);

    public enum AccessLevel
    {
        None,
        Read,
        Write
    }
    
    public Task<AccessLevel?> GetPermissionsForContentAsync(Guid? userId, string name, CancellationToken ct);
    Task<IEnumerable<Entry>> GetAvailableContentAsync(Guid? userId, DateTimeOffset beforeOrAt, int limit,
        CancellationToken ct);
    Task<Contents?> GetContentAsync(Guid? userId, string name, CancellationToken ct);
    Task<bool> SetContentAsync(Guid? userId, string name, string content, CancellationToken ct);
    Task<bool> DeleteContentAsync(Guid? userId, string name, CancellationToken ct);
}
