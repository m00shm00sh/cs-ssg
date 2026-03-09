namespace CsSsg.Src.Auth;

internal partial class JwtAuthorizationEndpointFilter(ILogger<JwtAuthorizationEndpointFilter> logger): IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        var sub = httpContext.User.TrySubjectUid;
        LogJwtUserId(logger, sub);
        if (sub is null)
            return Results.Unauthorized();
        return await next(context);
    }

    [LoggerMessage(LogLevel.Debug, "JWT User: {subjectId}")]
    static partial void LogJwtUserId(ILogger<JwtAuthorizationEndpointFilter> logger, Guid? subjectId);
}