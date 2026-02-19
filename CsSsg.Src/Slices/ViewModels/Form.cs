using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels;

public record AntiforgeryForm(AntiforgeryTokenSet Antiforgery);

public record Form(string Destination, AntiforgeryTokenSet Antiforgery) : AntiforgeryForm(Antiforgery);