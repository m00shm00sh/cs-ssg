using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Program;

namespace CsSsg.Src.User;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class RoutingExtensions
{
    extension(WebApplication app)
    {
        private void AddUserJsonRoutes(EnvironmentFeature envGate, string apiPrefix)
        {
            var apiGroup = app.MapGroup(apiPrefix);
            apiGroup.MapPost(LOGIN_ENDPOINT, PostUserLoginActionAsync);
            envGate.Gate(EnvironmentFeature.Dev, () =>
            {
                apiGroup.MapPost(SIGNUP_ENDPOINT, PostUserSignupActionAsync);
                apiGroup.MapDelete(USER_PREFIX + "/{name}", DeleteUserActionAsync)
                    .UseJwtBearerAuthentication();
            });
        }
    }

    private static async Task<IResult> PostUserLoginActionAsync(Request req, TokenService tokSvc, AppDbContext dbRepo,
        CancellationToken token)
    {
        var (loginResult, uid) = await DoPostUserLoginActionAsync(dbRepo, req, token);
        if (loginResult is not RedirectHttpResult)
            return loginResult;
        var response = new LoginResponse(uid, tokSvc.GenerateToken(uid));
        return TypedResults.Ok(response);
    }

    private static async Task<IResult> PostUserSignupActionAsync(Request req, TokenService tokSvc, AppDbContext dbRepo,
        CancellationToken token)
    {
        var (signupResult, uid) = await DoPostUserSignupActionAsync(dbRepo, req, token);
        if (signupResult is not RedirectHttpResult)
            return signupResult;
        var response = new LoginResponse(uid, tokSvc.GenerateToken(uid));
        return TypedResults.Ok(response);
    }

    private static Task<IResult> DeleteUserActionAsync(string name, ClaimsPrincipal auth,
        AppDbContext dbRepo, CancellationToken token)
    {
        var uidFromAuth = auth.RequireUid;
        return DoDeleteUserAsync(uidFromAuth, name, dbRepo, token);
        // it is API-user responsibility to invalidate the token since it now refers to stale credentials
    }
}
