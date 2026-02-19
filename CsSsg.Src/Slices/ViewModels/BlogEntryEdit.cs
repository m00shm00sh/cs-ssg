using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;

namespace CsSsg.Src.Slices.ViewModels;

// NOTE: InitialContents is the raw Markdown and not a safe HTML render; ensure it doesn't bypass Razor's HTML sanitizer
public record BlogEntryEdit(HtmlString? PreviewHtml, string Title, string MarkdownContents,
    string ToPreviewPage, string ToSubmitPage, AntiforgeryTokenSet Antiforgery)
    : AntiforgeryForm(Antiforgery);