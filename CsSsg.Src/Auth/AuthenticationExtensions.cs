using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace CsSsg.Src.Auth;

internal static class AuthenticationExtensions
{
    // ReSharper disable once InconsistentNaming
    internal const string UID_CLAIM_NAME = "uid";

    extension(ClaimsPrincipal? auth)
    {
        private Guid? TryUidForType(params string[] types)
        {
            foreach (var type in types)
            {
                if (Guid.TryParse(auth?.FindFirstValue(type), out var uid))
                    return uid;
            }

            return null;
        }

// nullability analysis bug with extension members resolved in a later C# 14 version
#pragma warning disable CS8620
        public Guid? TryCookieUid
            => auth?.TryUidForType(UID_CLAIM_NAME);

        public Guid? TrySubjectUid
            => auth.TryUidForType(JwtRegisteredClaimNames.Sub);

        internal Guid? TryAnyUid
            => auth.TryUidForType(UID_CLAIM_NAME, JwtRegisteredClaimNames.Sub);

        public Guid RequireUid
            => auth?.TryAnyUid
               ?? throw new InvalidOperationException("valid uid not found (did you forget an authorization filter)");
#pragma warning restore CS8620
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

    extension(WebApplicationBuilder builder)
    {
        // we need a no-op default authentication that short circuitedly fails when both cookies and jwt are registered
        public void AddDefaultForbid()
        {
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultChallengeScheme = "forbidScheme";
                options.DefaultForbidScheme = "forbidScheme";
                options.AddScheme<DefaultAuthenticationHandler>("forbidScheme", "Handle forbidden");
            });
        }
    }
}

file class DefaultAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    [Obsolete("Obsolete")]
    public DefaultAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, ISystemClock clock) 
        : base(options, logger, encoder, clock)
    { }
    
    // ReSharper disable once ConvertToPrimaryConstructor
    public DefaultAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, 
        UrlEncoder encoder) : base(options, logger, encoder)
    { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        => AuthenticateResult.NoResult();
}