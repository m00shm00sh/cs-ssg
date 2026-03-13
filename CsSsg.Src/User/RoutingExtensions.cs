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
    private const string CHECK_AUTH_ENDPOINT = "/user/checkauth";
    
    extension(WebApplication app)
    {
        public void AddUserRoutes(string apiPrefix)
        {
            app.AddUserHtmlRoutes();
            app.AddUserJsonRoutes(apiPrefix);

            if (app.Environment.IsDevelopment())
            {
                app.MapGet(CHECK_AUTH_ENDPOINT, CheckAuthentication);
            }
        }
    }
    
    public static async Task<(IResult, Guid)> DoPostUserLoginActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
        => (await dbRepo.LoginUserAsync(req, token)).Match<(IResult, Guid)>(
            /* Failure */ _ => (TypedResults.Forbid(), Guid.Empty),
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid)
        );
    
    public static async Task<(IResult, Guid)> DoPostUserSignupActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
        => (await dbRepo.CreateUserAsync(req, token)).Match<(IResult, Guid)>(
            failCode => (TypedResults.BadRequest(failCode), Guid.Empty),
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid)
        );

    public static async Task<Results<Ok<UserEntry>, ForbidHttpResult>>
    DoGetUserModifyPageAsync(Guid uid, AppDbContext dbRepo, CancellationToken token)
    {
        var entry = await dbRepo.FindEntryForUserAsync(uid, token);
        // the value can be null if the user was deleted after login without the cookie getting invalidated
        if (entry.IsNone)
            return TypedResults.Forbid();
        return TypedResults.Ok((UserEntry)entry);
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

    private static Results<ForbidHttpResult, Ok> CheckAuthentication(ClaimsPrincipal? auth)
    {
        var uid = auth.TryAnyUid;
        if (uid is null)
            return TypedResults.Forbid();
        return TypedResults.Ok();
    }
}
