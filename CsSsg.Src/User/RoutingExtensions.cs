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
    private const string LOGIN_ENDPOINT = "/user/login";
    private const string LOGIN_ACTION = LOGIN_ENDPOINT + ".1";
    private const string SIGNUP_ENDPOINT = "/user/signup";
    private const string SIGNUP_ACTION = SIGNUP_ENDPOINT + ".1";
    private const string UPDATE_ENDPOINT = "/user/update";
    private const string UPDATE_ACTION = UPDATE_ENDPOINT + ".1";

    extension(WebApplication app)
    {
        public void AddUserRoutes()
        {
            app.MapGet(LOGIN_ENDPOINT, GetUserLoginPageAsync);

            app.MapPost(LOGIN_ACTION, PostUserLoginActionAsync);

            if (app.Environment.IsDevelopment())
            {
                app.MapGet(SIGNUP_ENDPOINT, GetUserSignupPageAsync);

                app.MapPost(SIGNUP_ACTION, PostUserSignupActionAsync);
            }

            app.MapGet("/user/modify", GetUserModifyPageAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>();

            app.MapPost(UPDATE_ACTION, PostUserModifyActionAsync)
                .AddEndpointFilter<RequireUidEndpointFilter>();
        }
    }
    
    private static RazorSliceHttpResult<Form> GetUserLoginPageAsync(HttpContext ctx, IAntiforgery af)
        => DoGetUserLoginPageAsync(af.GetAndStoreTokens(ctx));
    
    public static RazorSliceHttpResult<Form> DoGetUserLoginPageAsync(AntiforgeryTokenSet aft)
        => Results.Extensions.RazorSlice<LoginView, Form>(new LoginForm(LOGIN_ACTION, aft));

    private static async Task<IResult> PostUserLoginActionAsync(HttpContext ctx, IAntiforgery af, AppDbContext dbRepo,
        [FromForm] string email, [FromForm] string password, CancellationToken token)
    {
        var (result, uid) = await DoPostUserLoginActionAsync(dbRepo, new Request(email, password), token);
        await ctx.CreateSignedInUidCookie(uid);
        return result;
    }
    
    public static async Task<(IResult, Guid)> DoPostUserLoginActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
        => (await dbRepo.LoginUserAsync(req, token)).Match<(IResult, Guid)>(
            failCode => (TypedResults.Forbid(), Guid.Empty),
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid)
        );
    private static RazorSliceHttpResult<Form> GetUserSignupPageAsync(HttpContext ctx, IAntiforgery af)
        => DoGetUserSignupPageAsync(af.GetAndStoreTokens(ctx));
    
    public static RazorSliceHttpResult<Form> DoGetUserSignupPageAsync(AntiforgeryTokenSet aft)
        => Results.Extensions.RazorSlice<LoginView, Form>(new SignupForm(SIGNUP_ACTION, aft));

    private static async Task<IResult> PostUserSignupActionAsync(HttpContext ctx, IAntiforgery af, AppDbContext dbRepo,
        [FromForm] string email, [FromForm] string password, CancellationToken token)
    {
        var (result, uid) = await DoPostUserSignupActionAsync(dbRepo, new Request(email, password), token);
        await ctx.CreateSignedInUidCookie(uid);
        return result;
    }
    
    public static async Task<(IResult, Guid)> DoPostUserSignupActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
        => (await dbRepo.CreateUserAsync(req, token)).Match<(IResult, Guid)>(
            failCode => (TypedResults.BadRequest(failCode), Guid.Empty),
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid)
        );

    private static Task<Results<RazorSliceHttpResult<UpdateDetails>, ForbidHttpResult>>
    GetUserModifyPageAsync(HttpContext ctx, IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo,
        CancellationToken token)
    {
        var uid = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        return DoGetUserModifyPageAsync(aft, uid, dbRepo, token);
    }
    
    public static async Task<Results<RazorSliceHttpResult<UpdateDetails>, ForbidHttpResult>>
    DoGetUserModifyPageAsync(AntiforgeryTokenSet aft, Guid uid, AppDbContext dbRepo, CancellationToken token)
    {
        var currentEmail = await dbRepo.FindEmailForUserAsync(uid, token);
        // the value can be null if the user was deleted after login without the cookie getting invalidated
        if (currentEmail.IsNone)
            return TypedResults.Forbid();
        return Results.Extensions.RazorSlice<UpdateDetailsView, UpdateDetails>(
            new UpdateDetails((string)currentEmail, UPDATE_ACTION, aft));
    }

    private static Task<Results<RedirectHttpResult, BadRequest, ForbidHttpResult>>
    PostUserModifyActionAsync(IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo, [FromForm] string email,
        [FromForm] string password, CancellationToken token)
    {
        var uid = auth.RequireUid;
        var details = new Request(email, password);
        return DoPostUserModifyActionAsync(uid, details, dbRepo, token);
    }
    
    public static async Task<Results<RedirectHttpResult, BadRequest, ForbidHttpResult>>
    DoPostUserModifyActionAsync(Guid uid, Request details, AppDbContext dbRepo, CancellationToken token)
    {
        var updateResult = await dbRepo.UpdateUserAsync(uid, details, token);
        // unwrap from monad to nullable so that we get the desired type inference
        return updateResult.ToNullable() switch
        {
            null =>
                TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX),
            Failure.NotPermitted =>
                TypedResults.Forbid(),
            Failure.NotFound or Failure.Conflict or Failure.TooLong =>
                TypedResults.BadRequest(),
            _ => throw new ArgumentOutOfRangeException(null, "unexpected failure code")
        };
    }
}
