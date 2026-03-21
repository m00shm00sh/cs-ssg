using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.Post;

public record ManageEntry(string SlugName, string Title, int Size, string ActionLink,
    bool InitiallyPublic, AntiforgeryTokenSet Antiforgery)
    : AntiforgeryForm(Antiforgery);