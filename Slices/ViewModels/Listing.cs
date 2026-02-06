using Microsoft.AspNetCore.Html;

namespace CsSsg.Slices.ViewModels;

public record struct ListingEntry(string Title, string Name, DateTime LastModified, bool CanDeleteOrMove);

// CanModify => CanNew | CanDelete
public record struct Listing(IEnumerable<ListingEntry> Entries, bool CanModify);