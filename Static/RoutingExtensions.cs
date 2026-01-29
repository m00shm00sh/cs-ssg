using Microsoft.Extensions.FileProviders;

namespace CsSsg.Static;

internal static class RoutingExtensions
{
    extension(WebApplication app)
    {
        public void AddStaticRoutes(string prefix)
        {
            
            app.UseStaticFiles(new StaticFileOptions
            {
            
                FileProvider = new PhysicalFileProvider(
                    Path.Combine(app.Environment.ContentRootPath, "public")
                ),
                RequestPath = $"/{prefix}"
            });
        }
    }
}