using CsSsg.Db;
using Microsoft.EntityFrameworkCore;
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

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetFromEnvironmentOrConfig(
                "DB_URL", "ConnectionStrings:DbUrl"))
        );

        if (builder.Environment.IsDevelopment())
        {
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();
        }

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