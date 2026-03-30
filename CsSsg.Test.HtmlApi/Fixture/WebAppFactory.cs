using System.Collections.ObjectModel;
using CsSsg.Src.Db;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

using CsSsg.Test.Db;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CsSsg.Test.HtmlApi.Fixture;

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
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DbUrl"] = dbFixture.ConnectionString 
            });
        });
        builder.ConfigureServices(services =>
        {
            services.ConfigureDbContext<AppDbContext>(dbFixture.ConfigureDbContextOptions);
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo("./keys"))
                .SetApplicationName("csssg-htmlapitest");
        });
        return base.CreateHost(builder);
    }
}