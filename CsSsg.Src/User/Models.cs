using CsSsg.Src.Db;

namespace CsSsg.Src.User;
using Role = (RoleNamespace, string);

/// <summary>
/// User request.
/// </summary>
/// <param name="Email">user email</param>
/// <param name="Password">user password</param>
public readonly record struct Request(string Email, string Password)
{
    /// <summary>
    /// Checks request for validity.
    /// <br/>
    /// Known constraints:
    /// <list>
    ///     <item>Email cannot be empty or only whitespace</item>
    ///     <item>Email cannot contain '/'</item>
    ///     <item>Password cannot be empty or only whitespace</item>
    /// </list>
    /// </summary>
    public bool IsValid()
        => !string.IsNullOrWhiteSpace(Email) &&
           !Email.Contains('/') &&
           !string.IsNullOrWhiteSpace(Password);
}

/// <summary>
/// User details.
/// </summary>
/// <param name="Email">user email</param>
/// <param name="CreatedAt">timestamp of user create</param>
/// <param name="UpdatedAt">timestamp of user modify</param>
/// <param name="Roles">user's roles</param>
public readonly record struct UserEntry(string Email, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    List<Role> Roles);

/// <summary>
/// Login response.
/// </summary>
/// <param name="Uid">user id</param>
/// <param name="Roles">user roles</param>
/// <param name="Token">user jwt access token</param>
public readonly record struct LoginResponse(Guid Uid, IReadOnlyCollection<Role> Roles, string Token);