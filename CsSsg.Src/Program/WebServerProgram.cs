using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;
using Microsoft.AspNetCore.Routing.Constraints;

using CsSsg.Src.Auth;
using CsSsg.Src.Db;
using CsSsg.Src.Exceptions;
using CsSsg.Src.Post;
using CsSsg.Src.Static;
using CsSsg.Src.User;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace CsSsg.Src.Program;

internal static class WebServerProgram
{
    private const string API_PREFIX = "/api/v1";
        
    public static void Run(string[] args)
    {
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

        builder.Services.AddDataProtection().ApplyBuilder(dpb =>
        {
            dpb.PersistKeysToFileSystem(new DirectoryInfo(builder.Environment.ContentRootPath + "../.keys"));
            dpb.SetApplicationName(builder.Environment.ApplicationName);
            if (bool.TryParse(
                    builder.Configuration.GetFromEnvironmentOrConfigOrNull(
                        "DPAPI_RO_KEY", "DpApi:ReadonlyKey"),
                    out var roKey)
                && roKey)
            {
                dpb.DisableAutomaticKeyGeneration();
            }
        });
        
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = new PathString("/login");
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            });
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = TokenService.MakeJwtValidationParameters(builder.Configuration);
            });

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
        app.UseAntiforgery();
        app.UseMiddleware<AntiforgeryFailureHandlerMiddleware>();
        app.UseExceptionHandler(c => { });
        app.AddStaticRoutes("s");
        // expose the antiforgery token generator for integration tests
        if (app.Environment.IsDevelopment())
            app.AddGetAntiforgeryTokenRoute();
        app.AddBlogRoutes();
        app.AddUserRoutes(API_PREFIX);
        app.Run();
    }
}