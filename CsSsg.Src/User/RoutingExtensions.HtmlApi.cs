using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Slices.User;
using CsSsg.Src.Slices.ViewModels;
using CsSsg.Src.Slices.ViewModels.User;

namespace CsSsg.Src.User;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    private const string LOGIN_ENDPOINT = "/auth/login";
    private const string LOGIN_ACTION = LOGIN_ENDPOINT + ".1";
    private const string SIGNUP_ENDPOINT = "/auth/signup";
    private const string SIGNUP_ACTION = SIGNUP_ENDPOINT + ".1";
    private const string UPDATE_ENDPOINT = "/user/update";
    private const string UPDATE_ACTION = UPDATE_ENDPOINT + ".1";
    private const string DELETE_ACTION = "/user/delete";

    extension(WebApplication app)
    {
        private void AddUserHtmlRoutes()
        {
            app.MapGet(LOGIN_ENDPOINT, GetUserLoginPageAsync);

            app.MapPost(LOGIN_ACTION, PostUserLoginHtmlActionAsync);

            if (app.Environment.IsDevelopment())
            {
                app.MapGet(SIGNUP_ENDPOINT, GetUserSignupPageAsync);

                app.MapPost(SIGNUP_ACTION, PostUserSignupHtmlActionAsync);
            }

            app.MapGet("/user/modify", GetUserModifyPageAsync)
                .UseCookieAuthentication();

            app.MapPost(UPDATE_ACTION, PostUserModifyActionAsync)
                .UseCookieAuthentication();

            app.MapPost(DELETE_ACTION, PostDeleteUserActionAsync)
                .UseCookieAuthentication();
        }
    }
    
    private static RazorSliceHttpResult<Form> GetUserLoginPageAsync(HttpContext ctx, IAntiforgery af)
    {
        var aft = af.GetAndStoreTokens(ctx);
        return Results.Extensions.RazorSlice<LoginView, Form>(new LoginForm(LOGIN_ACTION, aft));
    }
    
    private static async Task<IResult> PostUserLoginHtmlActionAsync(HttpContext ctx, IAntiforgery af,
        AppDbContext dbRepo, [FromForm] string email, [FromForm] string password, CancellationToken token)
    {
        var (result, uid) = await DoPostUserLoginActionAsync(dbRepo, new Request(email, password), token);
        await ctx.CreateSignedInUidCookie(uid);
        return result;
    }

    private static RazorSliceHttpResult<Form> GetUserSignupPageAsync(HttpContext ctx, IAntiforgery af)
    {
        var aft = af.GetAndStoreTokens(ctx);
        return Results.Extensions.RazorSlice<LoginView, Form>(new SignupForm(SIGNUP_ACTION, aft));
    }
    
    private static async Task<IResult> PostUserSignupHtmlActionAsync(HttpContext ctx, IAntiforgery af,
        AppDbContext dbRepo, [FromForm] string email, [FromForm] string password, CancellationToken token)
    {
        var (result, uid) = await DoPostUserSignupActionAsync(dbRepo, new Request(email, password), token);
        await ctx.CreateSignedInUidCookie(uid);
        return result;
    }
    
    private static async Task<Results<RazorSliceHttpResult<UpdateDetails>, ForbidHttpResult>>
    GetUserModifyPageAsync(HttpContext ctx, IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo,
        CancellationToken token)
    {
        var uid = auth.RequireUid;
        var aft = af.GetAndStoreTokens(ctx);
        var entryResult = await DoGetUserModifyPageAsync(uid, dbRepo, token);
        if (entryResult.Result is ForbidHttpResult _403)
            return _403;
        return Results.Extensions.RazorSlice<UpdateDetailsView, UpdateDetails>(
            new UpdateDetails(((Ok<UserEntry>)entryResult.Result).Value.Email, UPDATE_ACTION, DELETE_ACTION, aft));
    }

    private static Task<Results<RedirectHttpResult, BadRequest, ForbidHttpResult>>
    PostUserModifyActionAsync(IAntiforgery af, ClaimsPrincipal auth, AppDbContext dbRepo, [FromForm] string email,
            [FromForm] string password, CancellationToken token)
    {
        var uid = auth.RequireUid;
        var details = new Request(email, password);
        return DoPostUserModifyActionAsync(uid, details, dbRepo, token);
    }

    private static async Task<Results<RedirectHttpResult, BadRequest<string>, ForbidHttpResult>>
    PostDeleteUserActionAsync(IAntiforgery af, HttpContext ctx, ClaimsPrincipal auth, IFormCollection form, 
        AppDbContext dbRepo,
            CancellationToken token)
    {
        var uid = auth.RequireUid;
        var toDelete = FormHelpers.ExtractEmailFromDeleteForm(form);
        if (toDelete is null)
            return TypedResults.BadRequest("missing confirmation or old_email");
        var result = await DoDeleteUserAsync(uid, toDelete, dbRepo, token);
        await ctx.SignOutAsync(CookiesConfigurer.Scheme);
        return result switch
        {
            NoContent => TypedResults.Redirect("/"),
            ForbidHttpResult f => f,
            NotFound => TypedResults.BadRequest("not found"),
            _ => throw new InvalidOperationException($"unhandled result type {result.GetType()}")
        };
    }
}

file static class FormHelpers
{
    internal static string? ExtractEmailFromDeleteForm(IFormCollection form)
    {
        var confirmDelete = ((string?)form["cb_delete"])?.ToLower() == "on";
        var email = (string?)form["old_email"];
        return confirmDelete ? email : null;
    }
}

