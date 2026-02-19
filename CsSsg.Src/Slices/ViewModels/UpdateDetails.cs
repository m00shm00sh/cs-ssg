using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels;

public record UpdateDetails(string CurrentEmail, string Destination, AntiforgeryTokenSet Antiforgery)
    : Form(Destination, Antiforgery);
