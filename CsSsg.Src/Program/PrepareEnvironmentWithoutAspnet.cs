using Microsoft.Extensions.FileProviders;

namespace CsSsg.Src.Program;

internal interface IHostEnvironmentWithLoggerFactory : IHostEnvironment
{
    ILoggerFactory LoggerFactory { get; set; }    
}

internal static class ConsoleAppExtensions
{
    // provides a minimal IHostEnvironment that's adequate for console apps
    private static readonly Lazy<IHostEnvironment> HostEnvironment = new(() => new HostEnvironmentImpl());

    internal static readonly Lazy<IHostEnvironmentWithLoggerFactory> EnvironmentWithLoggerFactory = new(() =>
    {
        var env = HostEnvironment.Value;
        // move the lazy access out of the LoggerFactory.Create to remove a circular dependency
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            var loggingSection = Configuration?.Value.GetSection("Logging");
            if (loggingSection is not null)
                builder.AddConfiguration(loggingSection);
            builder.AddConsole();
            builder.AddDebug();
        });
        return new HostEnvironmentWithLoggerFactory(env)
        {
            LoggerFactory = loggerFactory
        };
    });

    // provides a configuration root that resembles ASP.NET's WebApplicationBuilder.Configuration
    internal static readonly Lazy<IConfigurationRoot> Configuration = new(() =>
    {
        var env = HostEnvironment.Value;
        var builder = new ConfigurationBuilder();
        builder.SetBasePath(env.ContentRootPath);
        builder.AddJsonFile("appsettings.json");
        builder.AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true);
        builder.AddEnvironmentVariables();
        return builder.Build();
    });
    
    private class HostEnvironmentImpl : IHostEnvironment
    {
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string EnvironmentName { get; set; }
        
        internal HostEnvironmentImpl()
        {
            // hacked together from ASP.NET builder defaults but it's adequate for our needs
            ApplicationName = typeof(ConsoleAppExtensions).Assembly.GetName().Name
                              ?? throw new InvalidOperationException("unexpected: null assembly name");
            ContentRootPath = Directory.GetCurrentDirectory();
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
            EnvironmentName = _getEnvironmentName();
       }
    }

    private static string _getEnvironmentName()
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(environment))
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrEmpty(environment))
            environment = "Production";
        return environment;
    }
}

file class HostEnvironmentWithLoggerFactory(IHostEnvironment env) : IHostEnvironmentWithLoggerFactory
{
    public required ILoggerFactory LoggerFactory { get; set; }

    public string ApplicationName
    {
        get => env.ApplicationName;
        set => env.ApplicationName = value;
    }

    public IFileProvider ContentRootFileProvider
    {
        get => env.ContentRootFileProvider;
        set => env.ContentRootFileProvider = value;
    }

    public string ContentRootPath
    {
        get => env.ContentRootPath;
        set => env.ContentRootPath = value;
    }

    public string EnvironmentName
    {
        get => env.EnvironmentName;
        set => env.EnvironmentName = value;
    }
}