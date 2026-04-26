using CsSsg.Src.Program;
using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Src.Auth;

/// <summary>
/// This middleware will short circuit failed antiforgery checks with HTTP 400.<br/>
/// Requests with missing or successful checks are allowed to proceed.
/// </summary>
internal class AntiforgeryFailureHandlerMiddleware(RequestDelegate next, EnvironmentFeature envGate)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var antiforgeryValidation = context.Features.Get<IAntiforgeryValidationFeature>();
        if (antiforgeryValidation?.IsValid == false)
        {
            #nullable disable
            var failLine = antiforgeryValidation!.Error.Message;
            #nullable enable
            context.Response.StatusCode = 400;
            var errMsg = envGate.Query(EnvironmentFeature.Dev)
                ? $"Failed to validate antiforgery: {failLine}.\r\n"
                : "Failed to validate antiforgery.\r\n";
            await context.Response.WriteAsync(errMsg);
        }
        else
            await next(context);
    }
}
