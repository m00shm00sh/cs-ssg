using CsSsg.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

using CsSsg.Db;
using CsSsg.Slices;
using CsSsg.Slices.ViewModels;

namespace CsSsg.User;

internal static class RoutingExtensions
{
    private const string LOGIN_ACTION = "/usr/login.1";
        
    extension(WebApplication app)
    {
        public void AddUserRoutes()
        {
            app.MapGet("/user/login", (HttpContext ctx, IAntiforgery af) =>
            {
                var aft = af.GetAndStoreTokens(ctx);
                return Results.Extensions.RazorSlice<LoginView, Login>(new Login(LOGIN_ACTION, aft));
            });

            app.MapPost(LOGIN_ACTION, async (HttpContext ctx, IAntiforgery af, AppDbContext dbRepo,
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
