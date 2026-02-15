using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Slices.ViewModels;

public record Form(string Destination, AntiforgeryTokenSet Antiforgery);