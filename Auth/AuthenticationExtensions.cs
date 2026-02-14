using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CsSsg.Auth;

internal static class AuthenticationExtensions
{
    private const string _uidClaimName = "uid";

    extension(ClaimsPrincipal? auth)
    {
        public Guid? TryUid
            => Guid.TryParse(auth?.Claims?.FirstOrDefault(c => c.Type == _uidClaimName)?.Value, out Guid uid)
                ? uid
                : null;
    }

    extension(HttpContext ctx)
    {
        public Task CreateSignedInUidCookie(Guid uid)
        {
            var auth = new ClaimsPrincipal(
                new ClaimsIdentity([
                    new Claim(_uidClaimName, uid.ToString())
                ], CookieAuthenticationDefaults.AuthenticationScheme)
            );
            return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, auth);
        }
    }
}