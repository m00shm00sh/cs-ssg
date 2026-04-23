using KotlinScopeFunctions;
using LanguageExt;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.User;

namespace CsSsg.Src.Media;

// TODO: this is copy pasted from the same filter in Src.Post with slightly different keys; we should address this
//       duplication

/// <summary>
/// A per-request filter that injects content access permissions into the request's context, or short circuits with
/// HTTP 404 if none exist.
/// </summary>
/// <param name="logger">class logger</param>
/// <param name="cache">shared cache</param>
/// <param name="repo">per-request database context</param>
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

        return await (await GetPermissionsAsync(name, uid, token)).MatchAsync(
            async permission =>
            {
                http.Features.Set(permission);
                http.Features.Set(this);
                return await next(context);
            },
            () => Results.NotFound()
        );
    }

    internal async ValueTask<Option<PostPermission>> GetPermissionsAsync(string slugName, Guid? uid,
        CancellationToken token)
    {
        LogContentAccessPermissionsNameUid(logger, slugName, uid);
        var canAccess = await cache.GetOrSetAsync(
            _accessKeyForUidAndName(uid, slugName),
            async _ => (await repo.GetMetadataForMediaAsync(uid, slugName, token))?.AccessLevel,
            tags: ["access.media"], token: token);
        LogContentAccessPermissionsCompletedNameUid(logger, slugName, uid, canAccess);
        if (canAccess is null)
            return null;
        UnexpectedEnumValueException.VerifyOrThrow(canAccess);
    #nullable disable
        var asPerm = canAccess.Let(p => new PostPermission(p.Value));
    #nullable restore
        return asPerm;
    }
    

    private static string _accessKeyForUidAndName(Guid? uid, string name)
        => $"access.media/{uid}/{name}";
    
    public static async Task InvalidateAccessCacheAsync(ILogger logger, IFusionCache cache, string context,
        CancellationToken token, ICollection<string>? extraKeys = null)
    {
        extraKeys ??= Array.Empty<string>();
        LogInvalidateAccessCaches(logger, context, extraKeys);
        await cache.RemoveByTagAsync(["access.media", ..extraKeys], token: token);
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

/// <summary>
/// A per-request filter that checks for write access or create permissions before allowing the request to proceed.
/// It checks for the following:
/// <list type="termdef">
///     <item>
///         <term>can write or create</term>
///         <description>proceed</description>
///     </item>
///     <item>
///         <term>attempt to create without permission</term>
///         <description>HTTP 404</description>
///     </item>
///     <item>
///         <term>attempt to write (for update) without permission</term>
///         <description>HTTP 403</description>
///     </item>
/// </list>
/// </summary>
/// <param name="logger">class logger</param>
/// <param name="cache">shared cache</param>
/// <param name="repo">per-request database context</param>
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

        return await (await VerifyPermissionAsync(permission, updateSlug, uid, token)).MatchAsync(
            /* IResult */ errorCode => errorCode,
            async () => await next(context)
        );
    }

    internal async ValueTask<Option<IResult>> VerifyPermissionAsync(AccessLevel? existingPermission, 
        string? updateSlugNameForLogger, Guid uid, CancellationToken token)
    {
        var canCreate = existingPermission is null && await repo.DoesUserHaveCreateMediaPermissionAsync(uid, token);
        
        LogWritePermissionsInvocation(logger, updateSlugNameForLogger, uid, existingPermission, canCreate);
        
        return existingPermission switch
        {
            null when !canCreate => // anonymous user tries to create new post
                Option<IResult>.Some(Results.NotFound()),
            AccessLevel.None or AccessLevel.Read => // user (known or anonymous) has permission but it is not write
                Option<IResult>.Some(Results.Forbid()),
            null when canCreate => // known user has create permission
                Option<IResult>.None,
            AccessLevel.Write or AccessLevel.WritePublic => // user has write permission
                Option<IResult>.None,
            _ => throw UnexpectedEnumValueException.Create(existingPermission)
        };
    }

    [LoggerMessage(LogLevel.Information, 
        "write access permissions: name={name}, uid={uid}, perm={perm} canCreate={canCreate}")]
    static partial void LogWritePermissionsInvocation(ILogger<WritePermissionFilter> logger,
        string? name, Guid uid, AccessLevel? perm, bool canCreate);
}

// class instead of readonly struct so that it can be nullable
internal record PostPermission(AccessLevel AccessLevel);