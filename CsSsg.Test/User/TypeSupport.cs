using System.Security.Claims;
using static CsSsg.Src.Auth.AuthenticationExtensions;
using CsSsg.Src.Db;
using CsSsg.Src.User;
using static CsSsg.Src.User.RepositoryExtensions;

namespace CsSsg.Test.User;

internal static class TypeSupport
{
    extension(UserClaims uc)
    {
        internal ClaimsPrincipal ToIdentity(string? scheme = null)
        => new(
                new ClaimsIdentity([
                    new Claim(UID_CLAIM_NAME, uc.Id.ToString()),
                    ..EncodeRoles(uc.Roles)
                ], scheme)
            );
    }
}

internal class UserClaimsEqualityComparer : IEqualityComparer<UserClaims>
{
    public static UserClaimsEqualityComparer Instance { get; } = new();
    
    public bool Equals(UserClaims? a, UserClaims? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Id != b.Id) return false;
        var aRoles = _sortedRoles(a);
        var bRoles = _sortedRoles(b);
        if (aRoles.Count != bRoles.Count) return false;
        if (aRoles.Except(bRoles).Any()) return false;
        return true;
    }

    public int GetHashCode(UserClaims obj)
        => throw new NotImplementedException();

    private static List<(RoleNamespace, string)> _sortedRoles(UserClaims uc)
    {
        List<(RoleNamespace, string)> roles;
        lock (uc)
        {
            uc.Roles.Sort();
            roles = uc.Roles;
        }
        return roles;
    }
}