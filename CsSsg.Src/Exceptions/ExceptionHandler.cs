using Microsoft.AspNetCore.Diagnostics;

namespace CsSsg.Src.Exceptions;

internal class ExceptionHandler(ILogger<ExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        logger.LogError("An exception occurred: <{excType}> {excMsg}", exception.GetType(), exception.Message);
        
        var status = 500;
        var message = "Server error";
        if (exception is ArgumentException)
        {
            status = 400;
            message = "Bad Request";
        }
        context.Response.StatusCode = status;
        await context.Response.WriteAsync(message, token);
        return true;
    }
    
}