using Microsoft.Extensions.FileProviders;

namespace CsSsg.Src.Static;

internal static class RoutingExtensions
{
    extension(IWebHostEnvironment env)
    {
        internal string ResolveSolutionContentRootPath()
        {
            // we need to cook root path a bit when invoked from integration tests; 
            // this should detect if running under test and adjust accordingly
            var applicationName = typeof(RoutingExtensions).Assembly.GetName().Name
                                  ?? throw new InvalidOperationException("unexpected: null assembly name");
            var contentRootPath = env.ContentRootPath;
            if (contentRootPath.EndsWith(applicationName))
                contentRootPath = Path.Combine(contentRootPath, "..");
            return contentRootPath;
        }
    }
    
    extension(WebApplication app)
    {
       
        public void AddStaticRoutes(string prefix)
        {
            var contentRootPath = Path.Combine(app.Environment.ResolveSolutionContentRootPath(), "public");
            app.Logger.LogInformation($"loading static from {contentRootPath}");
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(contentRootPath),
                RequestPath = $"/{prefix}"
            });
        }
    }
}