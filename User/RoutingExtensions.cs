using System.Security.Claims;
using CsSsg.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

using CsSsg.Db;
using CsSsg.Slices;
using CsSsg.Slices.ViewModels;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CsSsg.User;

internal static class RoutingExtensions
{
    private const string LOGIN_ACTION = "/user/login.1";
    private const string UPDATE_ACTION = "/user/update.1";
        
    extension(WebApplication app)
    {
        public void AddUserRoutes()
        {
            app.MapGet("/user/login", (HttpContext ctx, IAntiforgery af) =>
            {
                var aft = af.GetAndStoreTokens(ctx);
                return Results.Extensions.RazorSlice<LoginView, Form>(new(LOGIN_ACTION, aft));
            });

            app.MapPost(LOGIN_ACTION,
                async (HttpContext ctx, IAntiforgery af, AppDbContext dbRepo,
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
                return Results.Redirect(Post.RoutingExtensions.BLOG_PREFIX);
            });

            app.MapGet("/user/modify",
                async Task<Results<RazorSliceHttpResult<UpdateDetails>, ForbidHttpResult>>
                    (HttpContext ctx, IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo,
                    CancellationToken token) =>
                {
                    var uid = auth.TryUid!.Value;
                var currentEmail = await dbRepo.FindEmailForUserAsync(uid, token);
                // the value can be null if the user was deleted after login without the cookie getting invalidated
                if (currentEmail is null)
                    return TypedResults.Forbid();
                var aft = af.GetAndStoreTokens(ctx);
                return Results.Extensions.RazorSlice<UpdateDetailsView, UpdateDetails>(new(currentEmail, UPDATE_ACTION, aft));
            })
            .AddEndpointFilter<RequireUidEndpointFilter>();
            
            app.MapPost(UPDATE_ACTION,
                    async Task<Results<RedirectHttpResult, BadRequest, ForbidHttpResult>>
                        (IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo, [FromForm] string email,
                        [FromForm] string password, CancellationToken token) =>
                    {
                        var uid = auth.TryUid!.Value;
                        var details = new Request(email, password);
                        return await dbRepo.UpdateUserAsync(uid, details, token) switch
                        {
                            null =>
                                TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX),
                            Failure.NotPermitted =>
                                TypedResults.Forbid(),
                            Failure.NotFound or Failure.Conflict or Failure.TooLong =>
                                TypedResults.BadRequest(),
                            _ => throw new ArgumentOutOfRangeException()
                        };
                    })
                .AddEndpointFilter<RequireUidEndpointFilter>();
        }
    }
}
