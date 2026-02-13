namespace CsSsg.User;

internal readonly record struct Request(
    string Email,
    string Password);

