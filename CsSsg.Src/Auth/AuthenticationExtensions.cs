using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CsSsg.Src.Auth;

internal static class AuthenticationExtensions
{
    // ReSharper disable once InconsistentNaming
    private const string UID_CLAIM_NAME = "uid";

    extension(ClaimsPrincipal? auth)
    {
        public Guid? TryUid
            => Guid.TryParse(auth?.Claims.FirstOrDefault(c => c.Type == UID_CLAIM_NAME)?.Value, out Guid uid)
                ? uid
                : null;

        public Guid RequireUid
            => auth?.TryUid ?? throw new InvalidOperationException("valid uid not found");
    }

    extension(HttpContext ctx)
    {
        public Task CreateSignedInUidCookie(Guid uid)
        {
            var auth = new ClaimsPrincipal(
                new ClaimsIdentity([
                    new Claim(UID_CLAIM_NAME, uid.ToString())
                ], CookieAuthenticationDefaults.AuthenticationScheme)
            );
            return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, auth);
        }
    }
}