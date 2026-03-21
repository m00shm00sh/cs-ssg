using Microsoft.AspNetCore.Html;

namespace CsSsg.Src.Slices.ViewModels.Post;

public record struct BlogEntry(string Title, HtmlString Contents, string? ToEditPage);