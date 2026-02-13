using Microsoft.AspNetCore.Antiforgery;

namespace CsSsg.Auth;

internal class AntiforgeryFailureHandlerMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var antiforgeryValidation = context.Features.Get<IAntiforgeryValidationFeature>();
        if (antiforgeryValidation?.IsValid == false)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Failed to validate antiforgery.\r\n");
        }
        else
            await next(context);
    }
}
