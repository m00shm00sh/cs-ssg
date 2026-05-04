using CsSsg.Src.Filters;
using static CsSsg.Src.Post.IManageCommand;

namespace CsSsg.Src.Media;

/// <summary>
/// A listing entry representing a Media that can be accessed.
/// </summary>
/// <param name="Slug">Slug (link) name</param>
/// <param name="ContentType">mime content type</param>
/// <param name="Size">Media size</param>
/// <param name="IsPublic">Whether the media can be viewed anonymously</param>
/// <param name="AuthorHandle">Email of the user that is the post's current author</param>
/// <param name="LastModified">Timestamp of last modification</param>
/// <param name="AccessLevel">Access permissions (see <see cref="Filters.AccessLevel"/>)</param>
// NOTE: Entry is always returned from the RepositoryExtensions so there is no need to validate lengths
public readonly record struct Entry(
    string Slug, string ContentType, long Size,
    bool IsPublic, string AuthorHandle, DateTime LastModified, AccessLevel AccessLevel)
{
    
    /// Computes slug (link) name from filename
    public static string SlugifyFilename(string fileName)
        => RoutingExtensions.SlugifyFilename(fileName);
}

/// <summary>
/// Media contents
/// </summary>
public readonly record struct Object
{
    public Object(string contentType, Stream contentStream)
    {
        if (!contentStream.CanRead)
            throw new InvalidOperationException("contentStream must be a readable stream");
        ContentType = contentType;
        ContentStream = contentStream;
    }
    
    public string ContentType { get; private init; }
    public Stream ContentStream { get; private init; }

    /// <summary>
    /// If the supplied stream cannot seek, buffer it so it can be drained and have a usable Length property.
    /// If the buffering goes past a configured limit, return null.
    /// </summary>
    /// <param name="sizeLimit">read limit to fail after</param>
    /// <param name="token">cancellation token</param>
    /// <returns>a new Object buffering the current one or null</returns>
    internal async Task<Object?> BufferIfNotSeekableAsync(long sizeLimit, CancellationToken token)
    {
        if (ContentStream.CanSeek)
            return this;
        var stream = ContentStream.ConstructBufferingReadStream();
        if (await stream.TryDrainThenRewindAsync(sizeLimit, token))
            return this with { ContentStream = stream };
        return null;
    }
}

public record struct Stats(string ContentType, long Size, Permissions Permissions);