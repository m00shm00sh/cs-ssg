using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;

namespace CsSsg.Src.Auth;

internal static class BuilderExtensions
{
    extension(IDataProtectionBuilder builder)
    {
        public IDataProtectionBuilder ApplyBuilder(Action<IDataProtectionBuilder> block)
        {
            block(builder);
            return builder;
        }
    }
    
    // Helpers for selecting authentication schemes per endpoint (or group).
    extension(RouteHandlerBuilder routeBuilder)
    {
        private RouteHandlerBuilder UseAuthenticationScheme(string scheme, string claim)
        {
            routeBuilder.RequireAuthorization(auth =>
            {
                auth.AddAuthenticationSchemes(scheme);
                auth.RequireClaim(claim);
            });
            return routeBuilder;
        }

        public RouteHandlerBuilder UseCookieAuthentication()
            => routeBuilder.UseAuthenticationScheme(
                CookieAuthenticationDefaults.AuthenticationScheme, AuthenticationExtensions.UID_CLAIM_NAME);

        public RouteHandlerBuilder UseJwtBearerAuthentication()
            => routeBuilder.UseAuthenticationScheme(
                JwtBearerDefaults.AuthenticationScheme, JwtRegisteredClaimNames.Sub);
    }
}
