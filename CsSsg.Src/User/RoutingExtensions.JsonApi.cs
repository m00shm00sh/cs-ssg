using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using CsSsg.Src.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using CsSsg.Src.Db;

namespace CsSsg.Src.User;

[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{

    extension(WebApplication app)
    {
        private void AddUserJsonRoutes(string apiPrefix)
        {
            var apiGroup = app.MapGroup(apiPrefix);
            apiGroup.MapPost(LOGIN_ENDPOINT, PostUserLoginActionAsync);
            if (app.Environment.IsDevelopment())
            {
                apiGroup.MapPost(SIGNUP_ENDPOINT, PostUserSignupActionAsync);
            }
        }
    }

    private static async Task<IResult> PostUserLoginActionAsync(Request req, TokenService tokSvc, AppDbContext dbRepo,
        CancellationToken token)
    {
        var (loginResult, uid) = await DoPostUserLoginActionAsync(dbRepo, req, token);
        if (loginResult is ForbidHttpResult forbidResult)
            return forbidResult;
        var response = new LoginResponse(uid, tokSvc.GenerateToken(uid));
        return TypedResults.Ok(response);
    }

    private static async Task<IResult> PostUserSignupActionAsync(Request req, TokenService tokSvc, AppDbContext dbRepo,
        CancellationToken token)
    {
        var (signupResult, uid) = await DoPostUserSignupActionAsync(dbRepo, req, token);
        if (signupResult is BadRequest<Failure> badReq)
            return badReq;
        var response = new LoginResponse(uid, tokSvc.GenerateToken(uid));
        return TypedResults.Ok(response);
    }
}
