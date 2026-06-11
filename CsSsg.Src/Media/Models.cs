using CsSsg.Src.Filters;
using CsSsg.Src.Post;
using static CsSsg.Src.Post.IManageCommand;

namespace CsSsg.Src.Media;

/// <summary>
/// A listing entry representing a Media that can be accessed.
/// </summary>
/// <param name="Slug">Slug (link) name</param>
/// <param name="ContentType">mime content type</param>
/// <param name="Size">Media size</param>
/// <param name="AuthorHandle">Email of the user that is the post's current author</param>
/// <param name="LastModified">Timestamp of last modification</param>
/// <param name="AccessLevel">Post access permission level</param>
/// <param name="AuthorId">Author id (for permissions)</param>
/// <param name="Tags">Access tags</param>
/// <param name="RevisionCount">Content revision count</param>
/// <param name="Revisions">Content revisions list</param>
// NOTE: Entry is always returned from the RepositoryExtensions so there is no need to validate lengths
public readonly record struct Entry(
    string Slug, string ContentType, long Size,
    string AuthorHandle, DateTimeOffset LastModified,
    AccessLevel AccessLevel, Guid AuthorId, IReadOnlyCollection<string> Tags,
    int RevisionCount, IReadOnlyList<Revision> Revisions
) : IHasTags, IHasRevisionList, IHasRevisions<Revision>
{
    /// Computes slug (link) name from filename
    public static string SlugifyFilename(string fileName)
        => RoutingExtensions.SlugifyFilename(fileName);
}

/// <summary>
/// A revision entry representing a revision of a Medium.
/// </summary>
/// <param name="Id">revision id</param>
/// <param name="ContentType">MIME type of revision</param>
/// <param name="Size">length of data at revision</param>
/// <param name="AuthorHandle">revision author</param>
/// <param name="Created">revision creation time</param>
public readonly record struct Revision(
    Guid Id,
    string ContentType, long Size,
    string AuthorHandle, DateTimeOffset Created
) : IRevision;

/// <summary>
/// Media contents
/// </summary>
public readonly record struct Object
{
    public Object(string contentType, Stream contentStream, DateTimeOffset? lastModified = null)
    {
        if (!contentStream.CanRead)
            throw new InvalidOperationException("contentStream must be a readable stream");
        ContentType = contentType;
        ContentStream = contentStream;
        LastModified = lastModified;
    }
    
    public string ContentType { get; private init; }
    public Stream ContentStream { get; private init; }
    public DateTimeOffset? LastModified { get; private init; }

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

public record struct Stats(string ContentType, long Size, PostTags Tags);