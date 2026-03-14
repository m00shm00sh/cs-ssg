using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.User;

namespace CsSsg.Src.Post;

internal partial class ContentAccessPermissionFilter(
    ILogger<ContentAccessPermissionFilter> logger, IFusionCache cache, AppDbContext repo)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var uid = http.User.TryAnyUid;
        if (http.GetRouteValue("name") is not string name)
            throw new InvalidOperationException("unexpected: could not find route param \"name\" having type string");
        var token = http.RequestAborted;

        LogContentAccessPermissionsNameUid(logger, name, uid);
        var canAccess = await cache.GetOrSetAsync(
            _accessKeyForUidAndName(uid, name),
            async _ => await repo.GetPermissionsForContentAsync(uid, name, token),
            tags: ["access"], token: token);
        LogContentAccessPermissionsCompletedNameUid(logger, name, uid, canAccess);
        if (canAccess is null)
            return Results.NotFound();
        UnexpectedEnumValueException.VerifyOrThrow(canAccess);
        http.Features.Set(new PostPermission(canAccess.Value));
        http.Features.Set(this);
        return await next(context);
    }

    private static string _accessKeyForUidAndName(Guid? uid, string name)
        => $"access/{uid}/{name}";
    
    public static async Task InvalidateAccessCacheAsync(ILogger logger, IFusionCache cache, string context,
        CancellationToken token, ICollection<string>? extraKeys = null)
    {
        extraKeys ??= Array.Empty<string>();
        LogInvalidateAccessCaches(logger, context, extraKeys);
        await cache.RemoveByTagAsync(["access", ..extraKeys], token: token);
    }

    public static async Task InvalidateAccessCacheForKeyAsync(ILogger logger, IFusionCache cache, string context, 
        Guid uid, string name, CancellationToken token)
    {
        LogInvalidateAccessCacheForUidAndName(logger, context, name, uid);
        await cache.RemoveAsync(_accessKeyForUidAndName(uid, name), token: token);
    }

    [LoggerMessage(LogLevel.Information, "content access permissions: lookup: name={name}, uid={uid}")]
    static partial void LogContentAccessPermissionsNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid);
    [LoggerMessage(LogLevel.Information, 
        "content access permissions: lookup: name={name}, uid={uid}, permissions={perms}")]
    static partial void LogContentAccessPermissionsCompletedNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid, AccessLevel? perms);    
    
    [LoggerMessage(LogLevel.Information, "{context}: invalidate access caches; ek={extraKeys}")]
    static partial void LogInvalidateAccessCaches(ILogger logger,
        string context, IEnumerable<string> extraKeys);
    
    [LoggerMessage(LogLevel.Information, "{context}: invalidate access cache entry: name={name} uid={uid}")]
    static partial void LogInvalidateAccessCacheForUidAndName(ILogger logger,
        string context, string name, Guid? uid);

}

internal partial class WritePermissionFilter(
    ILogger<WritePermissionFilter> logger, AppDbContext repo)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var uid = http.User.RequireUid;
        var permission = http.Features.Get<PostPermission>()?.AccessLevel;
        var updateSlug = http.GetRouteValue("name") as string;
        if (updateSlug is null && permission.HasValue)
            throw new InvalidOperationException(
                "unexpected: could not find route param \"name\" but we have existing permissions");
        var token = http.RequestAborted;
        var canCreate = permission is null && await repo.DoesUserHaveCreatePermissionAsync(uid, token);
        
        LogWritePermissionsInvocation(logger, updateSlug, uid, permission, canCreate);
        
        return permission switch
        {
            null when !canCreate =>
                Results.NotFound(),
            AccessLevel.None or AccessLevel.Read =>
                Results.Forbid(),
            null when canCreate =>
                await next(context),
            AccessLevel.Write or AccessLevel.WritePublic =>
                await next(context),
            _ => throw UnexpectedEnumValueException.Create(permission)
        };
    }

    [LoggerMessage(LogLevel.Information, 
        "write access permissions: name={name}, uid={uid}, perm={perm} canCreate={canCreate}")]
    static partial void LogWritePermissionsInvocation(ILogger<WritePermissionFilter> logger,
        string? name, Guid uid, AccessLevel? perm, bool canCreate);
}

// class instead of readonly struct so that it can be nullable
internal record PostPermission(AccessLevel AccessLevel);