using CsSsg.Src.Slices.ViewModels.Post;
using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.Media;

public record MediaManageEntry(PostLayout Header, AntiforgeryTokenSet Antiforgery,
    string SlugName, string ContentType, long Size, bool InitiallyPublic,
    string RenameActionLink, string PermissionsActionLink, string AuthorActionLink, string DeleteActionLink)
    : AntiforgeryForm(Antiforgery);