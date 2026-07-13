using CsSsg.Src.Slices.ViewModels.Post;

namespace CsSsg.Src.Slices.ViewModels.Media;

public record MediaManageEntry(PostLayout Header, string SlugName, string ContentType, long Size, 
    ManageEntry.EditMetadataActionLinks? EditMetadata);