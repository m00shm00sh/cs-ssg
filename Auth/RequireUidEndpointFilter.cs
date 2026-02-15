namespace CsSsg.Auth;

internal class RequireUidEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var uid = httpContext.User.TryUid;
        if (uid is null)
            return Results.Unauthorized();
        return await next(context);
    }
}
