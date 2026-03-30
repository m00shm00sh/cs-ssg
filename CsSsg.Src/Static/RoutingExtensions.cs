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
            var rootPath = Path.Combine(contentRootPath, applicationName);
            if (contentRootPath.EndsWith(applicationName))
                rootPath = Path.Combine(contentRootPath, "..", applicationName);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(rootPath),
                RequestPath = $"/{prefix}"
            });
        }
    }
}