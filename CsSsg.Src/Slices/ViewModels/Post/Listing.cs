namespace CsSsg.Src.Slices.ViewModels.Post;

public record struct ListingEntry(
    string Title, string Name,
    string AuthorHandle, bool IsPublic, DateTimeOffset LastModified,
    string? ToManagePage);

// CanModify => CanNew | CanDelete
public record struct Listing(PostLayout Header, IEnumerable<ListingEntry> Entries);