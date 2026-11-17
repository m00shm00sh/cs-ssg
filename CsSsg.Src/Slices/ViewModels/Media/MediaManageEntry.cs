using CsSsg.Src.Post;
using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Slices.ViewModels.Media;

public record MediaManageEntry(
    PostLayout Header, 
    string SlugName, string ContentType, long Size, 
    IEnumerable<IRevision> Revisions,
    ManageEntry.EditMetadataActionLinks? EditMetadata);

public record MediaRevisionViewEntry(
    long Size,
    string ContentType,
    string LinkAtRevision);
