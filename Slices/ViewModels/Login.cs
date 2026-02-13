using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Slices.ViewModels;

public record struct Login(string LoginDestination, AntiforgeryTokenSet Antiforgery);