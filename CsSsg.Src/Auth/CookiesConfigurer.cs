using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

using CsSsg.Src.Program;

namespace CsSsg.Src.Auth;

internal static class CookiesConfigurer
{
    internal const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    extension(WebApplicationBuilder builder)
    {
        public void ConfigureCookies()
        {
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
            
            builder.Services.AddAuthentication()
                .AddCookie(Scheme, options =>
                {
                    options.LoginPath = new PathString("/user/login");
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);

                    options.Events.OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = 401;
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = 403;
                        return Task.CompletedTask;
                    };
                });
        }
    }
}