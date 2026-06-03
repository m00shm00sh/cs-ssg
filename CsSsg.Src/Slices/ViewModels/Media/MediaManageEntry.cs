using Microsoft.AspNetCore.Antiforgery;

using CsSsg.Src.Post;
using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Slices.ViewModels.Media;

public record MediaManageEntry(PostLayout Header, AntiforgeryTokenSet Antiforgery,
    string SlugName, string ContentType, long Size, IManageCommand.PostVisibility InitialVisibility,
    string RenameActionLink, string PermissionsActionLink, string AuthorActionLink, string DeleteActionLink)
    : AntiforgeryForm(Antiforgery)
{
    public IManageCommand.PostVisibility InitialVisibility { get; init; } =
        InitialVisibility != IManageCommand.PostVisibility.Public
        ? InitialVisibility
        : throw new ArgumentOutOfRangeException(nameof(InitialVisibility), "invalid: media && visibility=public");
}