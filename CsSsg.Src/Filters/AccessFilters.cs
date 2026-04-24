using CsSsg.Src.Auth;
using LanguageExt;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;

namespace CsSsg.Src.Filters;

/// <summary>
/// Saves the category and callback for a ContentAccessPermissionFilter to do its work.
/// </summary>
/// <param name="Category">filter's content category (eg media, post)</param>
/// <param name="GetPermissionsAsync">callback for access permissions check</param>
internal record ContentAccessPermissionFilterConfigurator(
    string Category,
    ContentAccessPermissionFilterConfigurator.GetPermissionsFromDatabaseAsync GetPermissionsAsync)
    : IEndpointFilter
{
    internal delegate ValueTask<AccessLevel?> GetPermissionsFromDatabaseAsync
        (AppDbContext db, string slug, Guid? uid, CancellationToken token); 
    
    /// <summary>
    /// Injects the <see cref="ContentAccessPermissionFilterConfigurator"/> into the current context.
    /// </summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        context.HttpContext.SetContentAccessPermissionFilterConfigurator(this);
        return await next(context);
    }
}

file static class ContentAccessPermissionsConfigExtensions
{
    private const string CONTENT_ACCESS_CONFIG_KEY = "ContentAccessPermissionsConfig";
    extension(HttpContext ctx)
    {
        internal ContentAccessPermissionFilterConfigurator? TryGetContentAccessPermissionFilterConfigurator()
        {
            if (!ctx.Items.TryGetValue(CONTENT_ACCESS_CONFIG_KEY, out var obj))
                return null;
            return obj as ContentAccessPermissionFilterConfigurator ?? null;
        }

        internal void SetContentAccessPermissionFilterConfigurator(ContentAccessPermissionFilterConfigurator config)
            => ctx.Items[CONTENT_ACCESS_CONFIG_KEY] = config;
    }
}

internal static class ContentAccessPermissionsLevelExtensions
{
    private const string CONTENT_ACCESS_LEVEL_KEY = "ContentAccessPermissionsLevel";
    extension(HttpContext ctx)
    {
        internal AccessLevel? TryGetAccessLevel()
        {
            if (!ctx.Items.TryGetValue(CONTENT_ACCESS_LEVEL_KEY, out var obj))
                return null;
            if (obj is AccessLevel accessLevel)
                return accessLevel;
            return null;
        }

        internal void SetAccessLevel(AccessLevel accessLevel)
            => ctx.Items[CONTENT_ACCESS_LEVEL_KEY] = accessLevel;
    }
}

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
        var config = http.TryGetContentAccessPermissionFilterConfigurator()
            ?? throw ExceptionHelpers.MissingExpectedMiddlewareException("ContentAccessPermissionFilter");
        var uid = http.User.TryAnyUid;
        if (http.GetRouteValue("name") is not string name)
            throw new InvalidOperationException("unexpected: could not find route param \"name\" having type string");
        var token = http.RequestAborted;

        return await (await GetPermissionsAsync(config, name, uid, token)).MatchAsync(
            async permission =>
            {
                http.SetAccessLevel(permission);
                return await next(context);
            },
            () => Results.NotFound()
        );
    }

    internal async ValueTask<Option<AccessLevel>> GetPermissionsAsync(
        ContentAccessPermissionFilterConfigurator config, string slugName, Guid? uid, CancellationToken token)
    {
        LogContentAccessPermissionsNameUid(logger, slugName, uid);
        var canAccess = await cache.GetOrSetAsync(
            _accessKeyForUidAndName(config, uid, slugName),
            async _ => await config.GetPermissionsAsync(repo, slugName, uid, token),
            tags: [_accessTag(config)], token: token);
        LogContentAccessPermissionsCompletedNameUid(logger, slugName, uid, canAccess);
        if (canAccess is null)
            return Option<AccessLevel>.None;
        UnexpectedEnumValueException.VerifyOrThrow(canAccess);
        return (AccessLevel)canAccess;
    }

    private static string _accessTag(ContentAccessPermissionFilterConfigurator config) =>
        $"access-{config.Category}";
    
    private static string _accessKeyForUidAndName(ContentAccessPermissionFilterConfigurator config, Guid? uid,
        string name)
        => $"{_accessTag(config)}/{uid}/{name}";
    
    public static async Task InvalidateAccessCacheAsync(ILogger logger, IFusionCache cache,
        ContentAccessPermissionFilterConfigurator config, string logContext, CancellationToken token,
        ICollection<string>? extraKeys = null)
    {
        extraKeys ??= Array.Empty<string>();
        LogInvalidateAccessCaches(logger, config.Category, logContext, extraKeys);
        await cache.RemoveByTagAsync([_accessTag(config), ..extraKeys], token: token);
    }

    public static async Task InvalidateAccessCacheForKeyAsync(ILogger logger, IFusionCache cache,
        ContentAccessPermissionFilterConfigurator config, string context, Guid uid, string name, CancellationToken token)
    {
        LogInvalidateAccessCacheForUidAndName(logger, config.Category, context, name, uid);
        await cache.RemoveAsync(_accessKeyForUidAndName(config, uid, name), token: token);
    }

    [LoggerMessage(LogLevel.Information, "content access permissions: lookup: name={name}, uid={uid}")]
    static partial void LogContentAccessPermissionsNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid);
    [LoggerMessage(LogLevel.Information, 
        "content access permissions: lookup: name={name}, uid={uid}, permissions={perms}")]
    static partial void LogContentAccessPermissionsCompletedNameUid(ILogger<ContentAccessPermissionFilter> logger,
        string name, Guid? uid, AccessLevel? perms);    
    
    [LoggerMessage(LogLevel.Information, "{category}/{context}: invalidate access caches; ek={extraKeys}")]
    static partial void LogInvalidateAccessCaches(ILogger logger,
        string category, string context, IEnumerable<string> extraKeys);
    
    [LoggerMessage(LogLevel.Information, "{category}/{context}: invalidate access cache entry: name={name} uid={uid}")]
    static partial void LogInvalidateAccessCacheForUidAndName(ILogger logger,
        string category, string context, string name, Guid? uid);
}


/// <summary>
/// Saves the category and callback for a WritePermissionFilter to do its work.
/// </summary>
/// <param name="Category">filter's content category (eg media, post)</param>
/// <param name="GetCreatePermissionsAsync">callback for access permissions check</param>
internal record WritePermissionFilterConfigurator(
    string Category,
    WritePermissionFilterConfigurator.DoesUserHaveCreatePermissionsFromDatabaseAsync GetCreatePermissionsAsync)
    : IEndpointFilter
{
    internal delegate ValueTask<bool> DoesUserHaveCreatePermissionsFromDatabaseAsync
        (AppDbContext db, Guid? uid, CancellationToken token);
    
    /// <summary>
    /// Injects the <see cref="WritePermissionFilterConfigurator"/> into the current context.
    /// </summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        context.HttpContext.SetWritePermissionFilterConfigurator(this);
        return await next(context);
    }
}

file static class WritePermissionsConfigExtensions
{
    private const string WRITE_CONFIG_KEY = "WritePermissionsConfig";
    extension(HttpContext ctx)
    {
        internal WritePermissionFilterConfigurator? TryGetWritePermissionFilterConfigurator()
        {
            if (!ctx.Items.TryGetValue(WRITE_CONFIG_KEY, out var obj))
                return null;
            return obj as WritePermissionFilterConfigurator ?? null;
        }

        internal void SetWritePermissionFilterConfigurator(WritePermissionFilterConfigurator config)
            => ctx.Items[WRITE_CONFIG_KEY] = config;
    }
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
/// <param name="repo">per-request database context</param>
internal partial class WritePermissionFilter(
    ILogger<WritePermissionFilter> logger, AppDbContext repo)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var uid = http.User.RequireUid;
        var config = http.TryGetWritePermissionFilterConfigurator()
            ?? throw ExceptionHelpers.MissingExpectedMiddlewareException("WritePermissionFilter");
        var permission = http.TryGetAccessLevel();
        var updateSlug = http.GetRouteValue("name") as string;
        if (updateSlug is null && permission.HasValue)
            throw new InvalidOperationException(
                "unexpected: could not find route param \"name\" but we have existing permissions");
        var token = http.RequestAborted;

        return await (await VerifyPermissionAsync(config, permission, updateSlug, uid, token)).MatchAsync(
            /* IResult */ errorCode => errorCode,
            async () => await next(context)
        );
    }

    internal async ValueTask<Option<IResult>> VerifyPermissionAsync(WritePermissionFilterConfigurator config,
        AccessLevel? existingPermission, string? updateSlugNameForLogger, Guid uid, CancellationToken token)
    {
        var canCreate = existingPermission is null && await config.GetCreatePermissionsAsync(repo, uid, token);
        
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

/// <summary>
/// Access levels for a Media.
/// </summary>
public enum AccessLevel
{
    /// no permissions
    None = 1,
    /// permitted to read 
    Read,
    /// permitted to modify
    Write,
    /// permitted to modify and post is public
    WritePublic
}

internal static class AccessLevelExtensions
{
    extension(AccessLevel al)
    {
        public bool IsWrite => al is AccessLevel.Write or AccessLevel.WritePublic;
    }
}

file static class ExceptionHelpers
{
    public static InvalidOperationException MissingExpectedMiddlewareException(string filterName)
        => new($"{filterName} middleware expects its corresponding {filterName}Configurator to run"
               + " before Filter invocation");
}