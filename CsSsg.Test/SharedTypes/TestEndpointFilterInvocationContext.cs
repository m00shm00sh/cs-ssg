using Microsoft.AspNetCore.Http;

namespace CsSsg.Test.SharedTypes;

/// <summary>
/// Minimal <see cref="EndpointFilterInvocationContext"/> for exercising endpoint filters directly.
/// </summary>
internal sealed class TestEndpointFilterInvocationContext(HttpContext httpContext)
    : EndpointFilterInvocationContext
{
    public override HttpContext HttpContext { get; } = httpContext;

    public override IList<object?> Arguments { get; } = [];

    public override T GetArgument<T>(int index)
        => (T)Arguments[index]!;
}
