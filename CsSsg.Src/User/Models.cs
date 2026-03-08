namespace CsSsg.Src.User;

public readonly record struct Request(string Email, string Password);

public readonly record struct UserEntry(string Email, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
