using ZiggyCreatures.Caching.Fusion;

using CsSsg.Blog;
using CsSsg.Static;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(new FusionCacheEntryOptions
    {
        Duration = TimeSpan.FromMinutes(1)
    });

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSingleton<IContentSource, FileContentSource>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.AddStaticRoutes("s");
app.AddBlogRoutes();
app.Run();