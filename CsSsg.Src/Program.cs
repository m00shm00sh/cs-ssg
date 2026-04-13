using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.Post;
using CsSsg.Src.Program;
using CsSsg.Src.Static;
using CsSsg.Src.User;

const string API_PREFIX = "/api/v1";

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.Configure<RouteOptions>(options =>
    options.SetParameterPolicy<RegexInlineRouteConstraint>("regex")
);
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(new FusionCacheEntryOptions
    {
        Duration = TimeSpan.FromMinutes(1)
    });

builder.Services.AddAntiforgery();
builder.ConfigureCookies();
builder.ConfigureJwt();
builder.Services.AddAuthorization();
builder.Services.AddExceptionHandler<ExceptionHandler>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetFromEnvironmentOrConfig(
        "DB_URL", "ConnectionStrings:DbUrl"))
);
builder.Services.AddScoped<TokenService>();
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseMiddleware<AntiforgeryFailureHandlerMiddleware>(app.Environment);
app.UseExceptionHandler(_ => { });
app.AddStaticRoutes("s");
// expose the antiforgery token generator for integration tests
if (app.Environment.IsDevelopment())
    app.AddGetAntiforgeryTokenRoute();
app.AddBlogRoutes(API_PREFIX);
app.AddUserRoutes(API_PREFIX);
app.Run();