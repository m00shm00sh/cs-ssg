using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Post;

namespace CsSsg.Src.User;

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

    /// <summary>
    /// Logs in user by login details, returning a tuple of for-status <see cref="IResult"/> and <see cref="Guid"/>.
    /// </summary>
    /// <param name="dbRepo">request's database context</param>
    /// <param name="req">user login details</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a tuple of for-status <see cref="IResult"/> and <see cref="Guid"/></returns>
    public static async Task<(IResult, Guid)> DoPostUserLoginActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
    {
        if (!req.IsValid())
            return (TypedResults.BadRequest(), Guid.Empty);
        return (await dbRepo.LoginUserAsync(req, token)).Match<(IResult, Guid)>(
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid),
            /* Failure */ _ => (TypedResults.Forbid(), Guid.Empty));
    }
    
    /// <summary>
    ///     Signs up a new user given login details, returning a tuple of
    ///     for-status <see cref="IResult"/> and <see cref="Guid"/>.
    /// </summary>
    /// <param name="dbRepo">request's database context</param>
    /// <param name="req">new user login details</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>a tuple of for-status <see cref="IResult"/> and <see cref="Guid"/></returns>
    public static async Task<(IResult, Guid)> DoPostUserSignupActionAsync(AppDbContext dbRepo, Request req,
        CancellationToken token)
    {
        if (!req.IsValid())
            return (TypedResults.BadRequest(), Guid.Empty);
        return (await dbRepo.CreateUserAsync(req, token)).Match<(IResult, Guid)>(
            uid => (TypedResults.Redirect(Post.RoutingExtensions.BLOG_PREFIX), uid),
            failCode => (TypedResults.BadRequest(failCode), Guid.Empty));
    }

    /// <summary>
    ///     Get the <see cref="UserEntry"/>, which is then usable for a user modification view, if one exists.
    ///     In case of a race condition where user deletion doesn't correctly invalidate credentials,
    ///     a <see cref="ForbidHttpResult"/> is returned instead.
    /// </summary>
    /// <param name="uid">user id to query</param>
    /// <param name="token">async cancellation token</param>
    /// <param name="dbRepo">request's database context</param>
    /// <returns>
    ///     either an <see cref="Ok{UserEntry}"/> on success, or a <see cref="ForbidHttpResult"/> on failure
    /// </returns>
    public static async Task<Results<Ok<UserEntry>, ForbidHttpResult>>
    DoGetUserModifyPageAsync(Guid uid, AppDbContext dbRepo, CancellationToken token)
    {
        var entry = await dbRepo.FindEntryForUserAsync(uid, token);
        // the value can be null if the user was deleted after login without the cookie or token getting invalidated
        if (entry.IsNone)
            return TypedResults.Forbid();
        return TypedResults.Ok((UserEntry)entry);
    }

    /// <summary>
    /// Commits a modification of user details.
    /// </summary>
    /// <param name="uid">user id to update</param>
    /// <param name="details">new user details</param>
    /// <param name="dbRepo">request's database context</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>
    ///     <list>
    ///         <item>a <see cref="RedirectHttpResult"/> on success</item>
    ///         <item>a <see cref="ForbidHttpResult"/> if modification is not permitted</item>
    ///         <item>a <see cref="BadRequest"/> if the new user details or the user id are invalid</item>
    ///     </list>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">internal error on unhandled failure code</exception>
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
    
    /// <summary>
    /// Delete a user by email.
    /// </summary>
    /// <param name="uid">deleter's user id</param>
    /// <param name="deleteEmail">email of user to delete</param>
    /// <param name="dbRepo">request's database context</param>
    /// <param name="token">async cancellation token</param>
    /// <returns>
    ///     <list>
    ///         <item>a <see cref="NoContent"/> on success</item>
    ///         <item>a <see cref="ForbidHttpResult"/> if deletion is not permitted</item>
    ///         <item>a <see cref="NotFound"/> if the new user wasn't found</item>
    ///     </list>
    /// </returns>
    public static async Task<IResult /* 204 | 403 | 404 (transitive: 403 | 404) */>
        DoDeleteUserAsync(Guid uid, string deleteEmail, AppDbContext dbRepo, CancellationToken token)
    {
        var findDbUidResult = await dbRepo.FindUserByEmailAsync(deleteEmail, token);
        return await findDbUidResult.MatchAsync(async uidForEmail =>
        {
            if (uid != uidForEmail)
                return TypedResults.Forbid();
            var deleteResult = await dbRepo.DeleteUserAsync(uid, token);
            return deleteResult.Match(failCode => failCode.AsResult(),
                TypedResults.NoContent);
        }, failCode => failCode.AsResult());
    }

    /// Extract authentication extracted by middleware, if such exists.
    private static Results<UnauthorizedHttpResult, Ok> CheckAuthentication(ClaimsPrincipal? auth)
    {
        var uid = auth.TryAnyUid;
        if (uid is null || uid == Guid.Empty)
            return TypedResults.Unauthorized();
        return TypedResults.Ok();
    }
}
