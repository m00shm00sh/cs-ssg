using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.User;

public record LoginForm(string Destination, AntiforgeryTokenSet Antiforgery)
    : Form(Destination, "Login", Antiforgery);

public record SignupForm(string Destination, AntiforgeryTokenSet Antiforgery)
    : Form(Destination, "Signup", Antiforgery);

public record UpdateDetails(string CurrentEmail, string Destination, AntiforgeryTokenSet Antiforgery)
    : Form(Destination, "Update Details", Antiforgery);
