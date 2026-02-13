using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

using CsSsg.Db;
using CsSsg.Slices;
using CsSsg.Slices.ViewModels;
using CsSsg.User;

namespace CsSsg.Auth;

internal static class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddAuthRoutes()
        {
            app.MapGet("/login", (HttpContext ctx, IAntiforgery af) =>
            {
                var aft = af.GetAndStoreTokens(ctx);
                return Results.Extensions.RazorSlice<LoginView, Login>(new Login("/login.1", aft));
            });

            app.MapPost("/login.1", async (HttpContext ctx, IAntiforgery af, AppDbContext dbRepo,
                    [FromForm] string email, [FromForm] string password, CancellationToken token) =>
            {
                var req = new Request(email, password);
                var uid = Guid.Empty;
                (await dbRepo.LoginUserAsync(req, token)).Switch(
                    (Guid success) => uid = success,
                    (Failure _) => { }
                );
                if (uid == Guid.Empty)
                    return TypedResults.Forbid();
                await ctx.CreateSignedInUidCookie(uid);
                return Results.Redirect("/blog");
            });
        }
    }
}
