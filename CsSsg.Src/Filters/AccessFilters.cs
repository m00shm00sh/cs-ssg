using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using CsSsg.Src.Auth;
using LanguageExt;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.Post;

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
    internal delegate ValueTask<Post.RepositoryExtensions.PostPermissions?> GetPermissionsFromDatabaseAsync
        (AppDbContext db, string slug,  CancellationToken token); 
    
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
    private const string CONTENT_ACCESS_TAGS_KEY = "ContentAccessPermissionsTags";
    private const string CONTENT_ACCESS_CTOKEN_KEY = "ContentAccessPermissionsVersion";
    
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

        internal string[]? TryGetTags()
        {
            if (!ctx.Items.TryGetValue(CONTENT_ACCESS_TAGS_KEY, out var obj))
                return null;
            if (obj is string[] tags)
                return tags;
            return null;
        }
        
        internal void SetTags(string[] tags)
            => ctx.Items[CONTENT_ACCESS_TAGS_KEY] = tags;

        internal RepositoryExtensions.ConcurrencyToken? TryGetConcurrencyToken()
            => ctx.Items.TryGetValue(CONTENT_ACCESS_CTOKEN_KEY, out var tok)
                ? tok as RepositoryExtensions.ConcurrencyToken?
                : null;

        internal RepositoryExtensions.ConcurrencyToken RequireConcurrencyToken()
            => ctx.TryGetConcurrencyToken() ?? throw new InvalidOperationException("concurrency token not found");

        internal void SetConcurrencyToken(RepositoryExtensions.ConcurrencyToken token)
            => ctx.Items[CONTENT_ACCESS_CTOKEN_KEY] = token;

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
        var user = http.User;
        if (user.TryGetUid() is null)
            user = AuthenticationExtensions.NullUser;
        if (http.GetRouteValue("name") is not string name)
            throw new InvalidOperationException("unexpected: could not find route param \"name\" having type string");
        var token = http.RequestAborted;

        return await (await GetPermissionsAsync(config, name, user, token)).MatchAsync(
            async permission =>
            {
                var (level, writeTags, cToken) = permission;
                if (level == AccessLevel.None)
                    return Results.Forbid();
                http.SetAccessLevel(level);
                http.SetTags(writeTags);
                http.SetConcurrencyToken(cToken);
                return await next(context);
            },
            () => Results.NotFound()
        );
    }

    // returns an optional containing a tuple whose first field is access level and second field is the write tags
    // allowed (eg for cache invalidation)
    internal async ValueTask<Option<(AccessLevel, string[], RepositoryExtensions.ConcurrencyToken)>> GetPermissionsAsync(
        ContentAccessPermissionFilterConfigurator config, string slugName, ClaimsPrincipal user, CancellationToken token)
    {
        var uid = user.TryGetUid() ?? Guid.Empty;
        LogContentAccessPermissionsNameUid(logger, slugName, uid);
        var uidAndTags = await cache.GetOrSetAsync(
            _permsForName(config, slugName),
            async _ => await config.GetPermissionsAsync(repo, slugName, token),
            tags: [_accessTag(config)], token: token);

        if (uidAndTags is null)
            return Option<(AccessLevel, string[], RepositoryExtensions.ConcurrencyToken)>.None;
        
        var userTags = user.GetRoles(RoleNamespace.View, RoleNamespace.Edit)
            .Where(t => uidAndTags.Tags.Contains(t.Item2))
            .ToList();
        var readTags = userTags
            .Where(t => t.Item1 == RoleNamespace.View)
            .Select(t => t.Item2)
            .ToArray();
        var writeTags = userTags
            .Where(t => t.Item1 == RoleNamespace.Edit)
            .Select(t => t.Item2)
            .ToArray();
        // union of readTags and writeTags; it is simpler to iterate over the source list and just fetch the tags
        // since the filtering (which was the important bit) was already done
        var contentUserTags = userTags
            .Select(t => t.Item2)
            .ToArray();

        var canAccess = AccessLevel.None;
        if (uidAndTags.AuthorId == uid)
            canAccess = AccessLevel.FullControl;
        else if (writeTags.Length > 0)
            canAccess = AccessLevel.Write;
        else if (readTags.Length > 0)
            canAccess = AccessLevel.Read;

        LogContentAccessPermissionsCompletedNameUid(logger, slugName, uid, canAccess);
        
        return (canAccess, contentUserTags, uidAndTags.ConcurrencyToken);
    }

    private static string _accessTag(ContentAccessPermissionFilterConfigurator config) =>
        $"access-{config.Category}";
    
    private static string _permsForName(ContentAccessPermissionFilterConfigurator config, string name)
        => $"{_accessTag(config)}/{name}";
    
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
        await cache.RemoveAsync(_permsForName(config, name), token: token);
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
    WritePermissionFilterConfigurator.DoesUserHaveCreatePermissionsFromClaimsAsync GetCreatePermissionsAsync,
    AccessLevel[] AllowedAccessLevelsForExistingContent,
    bool ForbidCreate = false)
    : IEndpointFilter
{
    private static readonly AccessLevel[] DefaultAllowedAccess = [AccessLevel.Write, AccessLevel.FullControl];
    private static readonly AccessLevel[] ForbiddenAccess = [AccessLevel.None, AccessLevel.Read];

    public AccessLevel[] AllowedAccessLevelsForExistingContent
    {
        get;
        init => field = CheckAccessLevels(value);
    } = CheckAccessLevels(AllowedAccessLevelsForExistingContent);

    internal WritePermissionFilterConfigurator(
        string category, DoesUserHaveCreatePermissionsFromClaimsAsync getCreatePermissions)
        : this(category, getCreatePermissions, DefaultAllowedAccess) 
    { }

    [SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly")]
    private static AccessLevel[] CheckAccessLevels(AccessLevel[] accessLevels)
    {
        var forbidden = accessLevels.Intersect(ForbiddenAccess).ToList();
        if (forbidden.Count != 0)
            throw new ArgumentException("the access levels allow list contains forbidden value(s): "
                + string.Join(", ", forbidden)
                , nameof(AllowedAccessLevelsForExistingContent));
        return accessLevels;
    }
    
    internal delegate ValueTask<bool> DoesUserHaveCreatePermissionsFromClaimsAsync
        (AppDbContext db, ClaimsPrincipal? user, CancellationToken token);

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
        var user = http.User;
        user.RequireUid();
        var config = http.TryGetWritePermissionFilterConfigurator()
            ?? throw ExceptionHelpers.MissingExpectedMiddlewareException("WritePermissionFilter");
        var permission = http.TryGetAccessLevel();
        var updateSlug = http.GetRouteValue("name") as string;
        if (updateSlug is null && permission.HasValue)
            throw new InvalidOperationException(
                "unexpected: could not find route param \"name\" but we have existing permissions");
        var token = http.RequestAborted;

        return await (await VerifyPermissionAsync(config, permission, updateSlug, user, token)).MatchAsync(
            /* IResult */ errorCode => errorCode,
            async () => await next(context)
        );
    }

    internal async ValueTask<Option<IResult>> VerifyPermissionAsync(WritePermissionFilterConfigurator config,
        AccessLevel? existingPermission, string? updateSlugNameForLogger, ClaimsPrincipal? user, CancellationToken token)
    {
        var hasCreatePermission = existingPermission is null
                        && !config.ForbidCreate && await config.GetCreatePermissionsAsync(repo, user, token);

        // only used for logger so (null ?? default) is sensical here
        var uid = user.TryGetUid() ?? Guid.Empty;
        LogWritePermissionsInvocation(logger, updateSlugNameForLogger, uid, existingPermission, hasCreatePermission);
        
        var hasWritePermission = config.AllowedAccessLevelsForExistingContent
            .Contains(existingPermission ?? AccessLevel.None);
        
        if (existingPermission.HasValue)
            UnexpectedEnumValueException.VerifyOrThrow(existingPermission.Value);
        
        var contentWasFoundButAccessWasForbidden = existingPermission.HasValue && !hasWritePermission;
        
        return existingPermission switch
        {
            null when !hasCreatePermission =>
                Option<IResult>.Some(Results.NotFound()),
            null when hasCreatePermission =>
                Option<IResult>.None,
            _ when contentWasFoundButAccessWasForbidden =>
                Option<IResult>.Some(Results.Forbid()),
            _ when hasWritePermission =>
                Option<IResult>.None,
            _ =>
                throw new InvalidOperationException(
                    "unexpected: content not found and no forbid and no write permission")
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
    [SuppressMessage("ReSharper", "InconsistentNaming")] _reserved0 = 4,
    /// full control including change author and tags
    FullControl = 5,
}

internal static class AccessLevelExtensions
{
    extension(AccessLevel al)
    {
        public bool IsWrite => al is AccessLevel.Write or AccessLevel.FullControl;
    }
}

internal static class ExceptionHelpers
{
    public static InvalidOperationException MissingExpectedMiddlewareException(string filterName)
        => new($"{filterName} middleware expects its corresponding {filterName}Configurator to run"
               + " before Filter invocation");
}