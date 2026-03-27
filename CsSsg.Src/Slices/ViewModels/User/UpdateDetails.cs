using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.User;

public record UpdateDetails(string CurrentEmail, string Destination, string DeleteActionLink,
    AntiforgeryTokenSet Antiforgery)
    : Form(Destination, "Update Details", Antiforgery);
