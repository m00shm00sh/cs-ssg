using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Slices.ViewModels.User;

public record UpdateDetails(AntiforgeryTokenSet Antiforgery,
    string CurrentEmail, string Destination, string DeleteActionLink)
    : Form(Destination, "Update Details", Antiforgery);
