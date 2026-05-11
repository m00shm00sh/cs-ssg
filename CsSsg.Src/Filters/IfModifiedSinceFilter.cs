using CsSsg.Src.Auth;
using LanguageExt;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;

namespace CsSsg.Src.Filters;

/// <summary>
/// Saves the category and callback for a IfModifiedSinceFilter to do its work.
/// </summary>
/// <param name="Category">filter's content category (eg media, post)</param>
/// <param name="GetModifyTimeUtcAsync">callback for access permissions check</param>
internal record IfModifiedSinceFilterConfigurator(
    string Category,
    IfModifiedSinceFilterConfigurator.GetModifyTimeFromDatabaseAsync GetModifyTimeUtcAsync)
    : IEndpointFilter
{
    internal delegate ValueTask<DateTimeOffset?> GetModifyTimeFromDatabaseAsync
        (AppDbContext db, string slug, CancellationToken token); 
    
    /// <summary>
    /// Injects the <see cref="IfModifiedSinceFilterConfigurator"/> into the current context.
    /// </summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        context.HttpContext.SetIfmsFilterConfigurator(this);
        return await next(context);
    }
}

file static class IfModifiedSinceConfigExtensions
{
    private const string IFMS_CONFIG_KEY = "IfModifiedSinceConfig";
    
    extension(HttpContext ctx)
    {
        internal IfModifiedSinceFilterConfigurator? TryGetIfModifiedSinceFilterConfigurator()
        {
            if (!ctx.Items.TryGetValue(IFMS_CONFIG_KEY, out var obj))
                return null;
            return obj as IfModifiedSinceFilterConfigurator ?? null;
        }

        internal void SetIfmsFilterConfigurator(IfModifiedSinceFilterConfigurator config)
            => ctx.Items[IFMS_CONFIG_KEY] = config;
    }
}
internal static class ModifiedSinceValueExtensions
{
    private const string MS_KEY = "ModifiedSince";
    
    extension(HttpContext ctx)
    {
        internal DateTimeOffset? TryGetModifiedSinceValue()
        {
            if (!ctx.Items.TryGetValue(MS_KEY, out var obj))
                return null;
            return obj as DateTimeOffset? ?? null;
        }
        
        internal void SetModifiedSinceValue(DateTimeOffset? mTime)
            => ctx.Items[MS_KEY] = mTime;
    }
}

/// <summary>
/// A per-request filter that injects content access permissions into the request's context, or short circuits with
/// HTTP 404 if none exist.
/// </summary>
/// <param name="logger">class logger</param>
/// <param name="cache">shared cache</param>
/// <param name="repo">per-request database context</param>
internal partial class IfModifiedSinceFilter(
    ILogger<IfModifiedSinceFilter> logger, IFusionCache cache, AppDbContext repo)
    : IEndpointFilter
{
    public static readonly IResult NotModifiedResult = Results.StatusCode(StatusCodes.Status304NotModified);
    
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var config = http.TryGetIfModifiedSinceFilterConfigurator()
            ?? throw ExceptionHelpers.MissingExpectedMiddlewareException("IfModifiedSinceFilter");
        if (http.GetRouteValue("name") is not string name)
            throw new InvalidOperationException("unexpected: could not find route param \"name\" having type string");
        var token = http.RequestAborted;
        
        return await (await GetModifyTimeAsync(config, name, token)).MatchAsync(
            async mtime =>
            {
                mtime = mtime.TruncateSeconds();
                var ifms = http.Request.GetTypedHeaders().IfModifiedSince?.TruncateSeconds();
                if (ifms >= mtime)
                    return NotModifiedResult;
                var res = await next(context);
                if (http.TryGetModifiedSinceValue() is { } outgoingModifyTime)
                    http.Response.GetTypedHeaders().LastModified = outgoingModifyTime;
                return res;
            },
            () => Results.NotFound()
        );
    }

    internal async ValueTask<Option<DateTimeOffset>> GetModifyTimeAsync(
        IfModifiedSinceFilterConfigurator config, string slugName, CancellationToken token)
    {
        LogIfmsName(logger, slugName);
        var mtime = await cache.GetOrSetAsync(
            _mtimeKeyForName(config, slugName),
            async _ => await config.GetModifyTimeUtcAsync(repo, slugName, token),
            tags: [_ifmsTag(config)], token: token);
        LogIfmsCompletedName(logger, slugName, mtime);
        if (mtime is null)
            return Option<DateTimeOffset>.None;
        return mtime.Value;
    }

    private static string _ifmsTag(IfModifiedSinceFilterConfigurator config) =>
        $"ifms-{config.Category}";
    
    private static string _mtimeKeyForName(IfModifiedSinceFilterConfigurator config, string name)
        => $"{_ifmsTag(config)}/{name}";
    
    public static async Task InvalidateAccessCacheAsync(ILogger logger, IFusionCache cache,
        IfModifiedSinceFilterConfigurator config, string logContext, CancellationToken token,
        ICollection<string>? extraKeys = null)
    {
        extraKeys ??= Array.Empty<string>();
        LogInvalidateAccessCaches(logger, config.Category, logContext, extraKeys);
        await cache.RemoveByTagAsync([_ifmsTag(config), ..extraKeys], token: token);
    }

    public static async Task InvalidateAccessCacheForKeyAsync(ILogger logger, IFusionCache cache,
        IfModifiedSinceFilterConfigurator config, string context, string name, CancellationToken token)
    {
        LogInvalidateCacheForName(logger, config.Category, context, name);
        await cache.RemoveAsync(_mtimeKeyForName(config, name), token: token);
    }

    [LoggerMessage(LogLevel.Information, "ifms: lookup: name={name}")]
    static partial void LogIfmsName(ILogger<IfModifiedSinceFilter> logger,
        string name);
    [LoggerMessage(LogLevel.Information, 
        "ifms: lookup: name={name}, mtime={mtime}")]
    static partial void LogIfmsCompletedName(ILogger<IfModifiedSinceFilter> logger,
        string name, DateTimeOffset? mtime);    
    [LoggerMessage(LogLevel.Information, 
        "ifms: manual: name={name}, mtime={mtime}")]
    static partial void LogIfmsManuallyForName(ILogger<IfModifiedSinceFilter> logger,
        string name, DateTimeOffset? mtime);    
    
    [LoggerMessage(LogLevel.Information, "{category}/{context}: invalidate ifms caches; ek={extraKeys}")]
    static partial void LogInvalidateAccessCaches(ILogger logger,
        string category, string context, IEnumerable<string> extraKeys);
    
    [LoggerMessage(LogLevel.Information, "{category}/{context}: invalidate ifms cache entry: name={name}")]
    static partial void LogInvalidateCacheForName(ILogger logger,
        string category, string context, string name);
}

file static class DateTimeExtensions
{
    extension(DateTimeOffset dt)
    {
        public DateTimeOffset TruncateSeconds()
        => dt.AddTicks(-(dt.Ticks % TimeSpan.TicksPerSecond));
    }
}