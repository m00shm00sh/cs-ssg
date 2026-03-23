using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.Post;

public record ManageEntry(string SlugName, string Title, int Size, bool InitiallyPublic,
    string RenameActionLink, string PermissionsActionLink, string AuthorActionLink, string DeleteActionLink,
    AntiforgeryTokenSet Antiforgery)
    : AntiforgeryForm(Antiforgery);