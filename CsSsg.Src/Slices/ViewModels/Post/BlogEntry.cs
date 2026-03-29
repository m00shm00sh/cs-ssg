using Microsoft.AspNetCore.Html;

namespace CsSsg.Src.Slices.ViewModels.Post;

public record struct BlogEntry(PostLayout Header, string Title, HtmlString Contents, string? ToEditPage);