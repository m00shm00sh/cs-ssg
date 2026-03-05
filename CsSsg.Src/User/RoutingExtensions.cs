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
internal static partial class RoutingExtensions
{

    extension(WebApplication app)
    {
        public void AddUserRoutes()
        {
            app.AddUserHtmlRoutes();
        }
    }
    
    public static RazorSliceHttpResult<Form> DoGetUserLoginPageAsync(AntiforgeryTokenSet aft)
        => Results.Extensions.RazorSlice<LoginView, Form>(new LoginForm(LOGIN_ACTION, aft));

    public static async Task<(IResult, Guid)> DoPostUserLoginActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
        => (await dbRepo.LoginUserAsync(req, token)).Match<(IResult, Guid)>(
            failCode => (TypedResults.Forbid(), Guid.Empty),
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid)
        );
    
    public static RazorSliceHttpResult<Form> DoGetUserSignupPageAsync(AntiforgeryTokenSet aft)
        => Results.Extensions.RazorSlice<LoginView, Form>(new SignupForm(SIGNUP_ACTION, aft));

    public static async Task<(IResult, Guid)> DoPostUserSignupActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
        => (await dbRepo.CreateUserAsync(req, token)).Match<(IResult, Guid)>(
            failCode => (TypedResults.BadRequest(failCode), Guid.Empty),
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid)
        );

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
