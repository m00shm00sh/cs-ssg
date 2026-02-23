using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Auth;

internal static class AntiforgeryTokenRoute
{
    extension(WebApplication app)
    {
        internal void AddGetAntiforgeryTokenRoute()
        {
            app.MapGet("/aftoken", GetAntiforgeryTokenSet);
        }
    }

    private static AntiforgeryTokenSet GetAntiforgeryTokenSet(HttpContext ctx, IAntiforgery af)
    {
        var token = af.GetAndStoreTokens(ctx);
        return token;
    }
}