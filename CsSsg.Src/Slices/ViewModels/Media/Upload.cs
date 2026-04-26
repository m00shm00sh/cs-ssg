using CsSsg.Src.Slices.ViewModels.Post;
using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.Media;

public record Upload(PostLayout Header, AntiforgeryTokenSet Antiforgery, string ToSubmitPage, string? SlugName)
    : AntiforgeryForm(Antiforgery);