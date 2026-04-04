using Microsoft.Extensions.FileProviders;

namespace CsSsg.Src.Static;

internal static class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddStaticRoutes(string prefix)
        {
            // we need to cook root path a bit when invoked from integration tests; 
            // this should detect if running under test and adjust accordingly
            var applicationName = typeof(RoutingExtensions).Assembly.GetName().Name
                                  ?? throw new InvalidOperationException("unexpected: null assembly name");
            var contentRootPath = app.Environment.ContentRootPath;
            if (contentRootPath.EndsWith(applicationName))
                contentRootPath = Path.Combine(contentRootPath, "..", "public");
            else
                contentRootPath = Path.Combine(contentRootPath, "public");
            app.Logger.LogInformation($"loading static from {contentRootPath}");
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(contentRootPath),
                RequestPath = $"/{prefix}"
            });
        }
    }
}