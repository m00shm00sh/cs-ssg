using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.User;

public record LoginSignupForm(string LoginDestination, string? SignupDestination, AntiforgeryTokenSet Antiforgery)
    : AntiforgeryForm(Antiforgery);

public record UpdateDetails(string CurrentEmail, string Destination, string DeleteActionLink,
    AntiforgeryTokenSet Antiforgery)
    : Form(Destination, "Update Details", Antiforgery);
