using CsSsg.Src.Post;
using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.Post;

public record ManageEntry(
    PostLayout Header,
    string SlugName, string Title, int Size,
    IEnumerable<IRevision> Revisions,
    ManageEntry.EditMetadataActionLinks? EditMetadata)
{
    public record EditMetadataActionLinks(
        AntiforgeryTokenSet Antiforgery,
        IManageCommand.PostVisibility InitialVisibility,
        string RenameActionLink, string PermissionsActionLink, string AuthorActionLink, string DeleteActionLink,
        ICollection<IManageCommand.PostVisibility>? ForbiddenVisibilities = null
    ) : AntiforgeryForm(Antiforgery);
}

public record PostRevisionViewEntry(
    int ContentLength,
    string Title,
    string LinkAtRevision);