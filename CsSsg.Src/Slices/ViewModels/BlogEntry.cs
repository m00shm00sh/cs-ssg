using Microsoft.AspNetCore.Html;

namespace CsSsg.Src.Slices.ViewModels;

public record struct BlogEntry(string Title, HtmlString Contents, string? ToEditPage);