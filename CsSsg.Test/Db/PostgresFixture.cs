using System.Reflection;
using CsSsg.Src.Db;
using DbUp;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace CsSsg.Test.Db;

public class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer Container = null!;
    public DbContextOptions<AppDbContext> DbContextOptions = null!;
    
    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        Container = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("md_blog")
            .Build();
        await Container.StartAsync();
        var connectionString = Container.GetConnectionString();
        var optionsBuilder =  new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(Container.GetConnectionString());
        DbContextOptions = optionsBuilder.Options;
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .LogToConsole()
            .Build();
        var migrateResult = upgrader.PerformUpgrade();
        if (!migrateResult.Successful)
            throw new XunitException($"Could not execute PostgreSQL migration scripts: {migrateResult.Error}");
    }

    public void ConfigureDbContextOptions(DbContextOptionsBuilder builder)
    {
    }

    public Task DisposeAsync()
        => Container.DisposeAsync().AsTask();
}