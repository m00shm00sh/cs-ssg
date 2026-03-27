using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.User;

public record LoginSignupForm(string LoginDestination, string? SignupDestination, AntiforgeryTokenSet Antiforgery)
    : AntiforgeryForm(Antiforgery);
