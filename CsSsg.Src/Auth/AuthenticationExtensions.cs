using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CsSsg.Src.Auth;

internal static class AuthenticationExtensions
{
    // ReSharper disable once InconsistentNaming
    internal const string UID_CLAIM_NAME = "uid";

    extension(ClaimsPrincipal? auth)
    {
        internal Guid? TryUidForType(params string[] types)
        {
            foreach (var type in types)
            {
                if (Guid.TryParse(auth?.FindFirstValue(type), out var uid))
                    return uid;
            }

            return null;
        }

        public Guid? TryCookieUid
            => auth?.TryUidForType(UID_CLAIM_NAME);

        public Guid? TrySubjectUid
            => auth.TryUidForType(JwtRegisteredClaimNames.Sub);

        internal Guid? TryAnyUid
            => auth.TryUidForType(UID_CLAIM_NAME, JwtRegisteredClaimNames.Sub);

        public Guid RequireUid
            => auth?.TryAnyUid ?? throw new InvalidOperationException("valid uid not found");
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