using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Slices;
using CsSsg.Src.Slices.ViewModels;
using LanguageExt.Pipes;

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
    
    public static async Task<(IResult, Guid)> DoPostUserLoginActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
        => (await dbRepo.LoginUserAsync(req, token)).Match<(IResult, Guid)>(
            failCode => (TypedResults.Forbid(), Guid.Empty),
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
}
