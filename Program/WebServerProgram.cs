using ZiggyCreatures.Caching.Fusion;

namespace CsSsg.Program;

using Blog;
using Static;

internal static class WebServerProgram
{
    public static void Run(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.AddFusionCache()
            .WithDefaultEntryOptions(new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMinutes(1)
            });

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        builder.Services.AddSingleton<MarkdownHandler>();
        builder.Services.AddSingleton<IContentSource, FileContentSource>();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.AddStaticRoutes("s");
        app.AddBlogRoutes();
        app.Run();
    }
}