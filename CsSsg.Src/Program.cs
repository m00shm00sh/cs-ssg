using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.Media;
using CsSsg.Src.Post;
using CsSsg.Src.Program;
using CsSsg.Src.Static;
using CsSsg.Src.User;

const string API_PREFIX = "/api/v1";

// solves flakey System.IO.IOException:
// The configured user limit (128) on the number of inotify instances has been reached, or the per-process limit
// on the number of open file descriptors has been reached. 
Environment.SetEnvironmentVariable("DOTNET_hostBuilder:reloadConfigOnChange", "false");

var builder = WebApplication.CreateSlimBuilder(args);
var flags = Features.ParseFeatureFlagsString(
    builder.Configuration.GetFromEnvironmentOrConfig("FEATURES", "Features"));
var envGate = EnvironmentFeature.FromEnvironment(builder.Environment);
builder.Services.Configure<RouteOptions>(options =>
    options.SetParameterPolicy<RegexInlineRouteConstraint>("regex")
);
flags.Gate(Features.JsonApi, () =>
{
    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });
    builder.ConfigureJwt();
    builder.Services.AddScoped<TokenService>();
    JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
});
builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(new FusionCacheEntryOptions
    {
        Duration = TimeSpan.FromMinutes(1)
    });
builder.AddDefaultForbid();
flags.Gate(Features.HtmlApi, () =>
{
    builder.Services.AddAntiforgery();
    builder.ConfigureCookies();
});
builder.Services.AddAuthorization();
builder.Services.AddExceptionHandler<ExceptionHandler>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetFromEnvironmentOrConfig(
        "DB_URL", "ConnectionStrings:DbUrl"))
);
envGate.Gate(EnvironmentFeature.Dev, () =>
{
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
});

var app = builder.Build();
envGate.Gate(EnvironmentFeature.Dev, () => app.MapOpenApi());
app.UseAuthentication();
app.UseAuthorization();
flags.Gate(Features.HtmlApi, () =>
{
    app.UseAntiforgery();
    app.UseMiddleware<AntiforgeryFailureHandlerMiddleware>(envGate);
    // expose the antiforgery token generator for integration tests
    envGate.Gate(EnvironmentFeature.Dev, app.AddGetAntiforgeryTokenRoute);
});
app.UseExceptionHandler(_ => { });
app.AddStaticRoutes("s");
app.AddBlogRoutes(flags, API_PREFIX);
app.AddMediaRoutes(flags, API_PREFIX);
app.AddUserRoutes(flags, envGate, API_PREFIX);
app.Run();