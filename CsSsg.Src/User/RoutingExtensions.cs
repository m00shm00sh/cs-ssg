using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Slices;
using CsSsg.Src.Slices.ViewModels;

namespace CsSsg.Src.User;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static class RoutingExtensions
{
    private const string LOGIN_ACTION = "/user/login.1";
    private const string UPDATE_ACTION = "/user/update.1";

    extension(WebApplication app)
    {
        public void AddUserRoutes()
        {
            app.MapGet("/user/login", GetUserLoginPageAsync);

            app.MapPost(LOGIN_ACTION, PostUserLoginActionAsync);

            app.MapGet("/user/modify", GetUserModifyPageAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>();

            app.MapPost(UPDATE_ACTION, PostUserModifyActionAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>();
        }
    }
    public static RazorSliceHttpResult<Form> GetUserLoginPageAsync(HttpContext ctx, IAntiforgery af)
    {
        var aft = af.GetAndStoreTokens(ctx);
        return Results.Extensions.RazorSlice<LoginView, Form>(new Form(LOGIN_ACTION, aft));
    }
    
    public static async Task<IResult> PostUserLoginActionAsync(HttpContext ctx, IAntiforgery af, AppDbContext dbRepo,
        [FromForm] string email, [FromForm] string password, CancellationToken token)
    {
        var req = new Request(email, password);
        var uid = Guid.Empty;
        (await dbRepo.LoginUserAsync(req, token)).Switch(
            (Guid success) => uid = success,
            (Failure _) => { }
        );
        if (uid == Guid.Empty) return TypedResults.Forbid();
        await ctx.CreateSignedInUidCookie(uid);
        return Results.Redirect(Post.RoutingExtensions.BLOG_PREFIX);
    }

    public static async Task<Results<RazorSliceHttpResult<UpdateDetails>, ForbidHttpResult>> GetUserModifyPageAsync(
        HttpContext ctx, IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo, CancellationToken token)
    {
        var uid = auth.RequiredUid;
        var currentEmail = await dbRepo.FindEmailForUserAsync(uid, token);
        // the value can be null if the user was deleted after login without the cookie getting invalidated
        if (currentEmail is null) return TypedResults.Forbid();
        var aft = af.GetAndStoreTokens(ctx);
        return Results.Extensions.RazorSlice<UpdateDetailsView, UpdateDetails>(
            new UpdateDetails(currentEmail, UPDATE_ACTION, aft));
    }
 
    public static async Task<Results<RedirectHttpResult, BadRequest, ForbidHttpResult>> PostUserModifyActionAsync(
        IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo, [FromForm] string email, [FromForm] string password,
        CancellationToken token)
    {
        var uid = auth.RequiredUid;
        var details = new Request(email, password);
        return await dbRepo.UpdateUserAsync(uid, details, token) switch
        {
            null => TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX),
            Failure.NotPermitted => TypedResults.Forbid(),
            Failure.NotFound or Failure.Conflict or Failure.TooLong => TypedResults.BadRequest(),
            _ => throw new ArgumentOutOfRangeException(null, "unexpected failure code")
        };
    }
}
