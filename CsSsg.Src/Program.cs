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
var flags = Features.ParseFeatureFlagsString(
    builder.Configuration.GetFromEnvironmentOrConfig("FEATURES", "Features"));
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
flags.Gate(Features.HtmlApi, () =>
{
    app.UseAntiforgery();
    app.UseMiddleware<AntiforgeryFailureHandlerMiddleware>(app.Environment);
    // expose the antiforgery token generator for integration tests
    if (app.Environment.IsDevelopment())
        app.AddGetAntiforgeryTokenRoute();
});
app.UseExceptionHandler(_ => { });
app.AddStaticRoutes("s");
app.AddBlogRoutes(flags, API_PREFIX);
app.AddUserRoutes(flags, API_PREFIX);
app.Run();