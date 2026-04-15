using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using CsSsg.Src.Db;
using CsSsg.Src.Program;

using CsSsg.Test.Db;

namespace CsSsg.Test.JsonApi.Fixture;

internal class WebAppFactory(ITestOutputHelper outputHelper, PostgresFixture dbFixture) : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.AddXUnit(outputHelper);
        });
        // see https://github.com/dotnet/aspnetcore/issues/37680#issuecomment-1032922656
        builder.ConfigureHostConfiguration(config =>
        {
            byte[] randomBytes = new byte[64];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(randomBytes);
            }
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DbUrl"] = dbFixture.ConnectionString,
                ["Features"] = Features.JsonApi,
                ["Jwt:Issuer"] = "csssg-v1-jsonapitest",
                ["Jwt:Secret"]  = Convert.ToBase64String(randomBytes)
            });
        });
        builder.ConfigureServices(services =>
        {
            services.ConfigureDbContext<AppDbContext>(dbFixture.ConfigureDbContextOptions);
        });
        return base.CreateHost(builder);
    }
}