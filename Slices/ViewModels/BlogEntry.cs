using Microsoft.AspNetCore.Html;

namespace CsSsg.Slices.ViewModels;

public record struct BlogEntry(string Title, HtmlString Contents, string? ToEditPage);