using CsSsg.Src.Post;
using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Slices.ViewModels.Media;

public record struct MediaListingEntry(
    string Name, string Link, string ContentType, long Size,
    string AuthorHandle, IManageCommand.PostTags Tags, DateTimeOffset LastModified,
    string? ToManagePage);

public record struct MediaListing(PostLayout Header, IEnumerable<MediaListingEntry> Entries, string ToNewPage);