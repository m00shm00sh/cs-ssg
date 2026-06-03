using CsSsg.Src.Post;
using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.Post;

public record ManageEntry(PostLayout Header, AntiforgeryTokenSet Antiforgery,
    string SlugName, string Title, int Size, IManageCommand.PostVisibility InitialVisibility,
    string RenameActionLink, string PermissionsActionLink, string AuthorActionLink, string DeleteActionLink)
    : AntiforgeryForm(Antiforgery);