using Microsoft.Extensions.Configuration;

using CsSsg.Src.User;
using Microsoft.Extensions.Logging;
using Tommy.Extensions.Configuration;

namespace CsSsg.ConsoleLoader;

internal record struct ParsedConfig(string Server, Request Login, DirCommand[] Dir, FileCommand[] File);

internal record struct Login(string Email, string Password);

internal record struct DirCommand(string Path, Type Type);

internal enum Type
{
    Content,
    Media
}

internal record struct FileCommand(string Path, Type Type);

internal static class Config
{
    internal static (ParsedConfig, ILoggerFactory) Parse(string configFilePath)
    {
        var b = new ConfigurationBuilder();
        b.SetBasePath(Directory.GetCurrentDirectory());
        b.AddTomlFile(configFilePath);
        b.AddEnvironmentVariables();
        var c = b.Build();
        var parsed = c.Get<ParsedConfig>();
        var factory = LoggerFactory.Create(builder =>
        {
            var loggingSection = c.GetSection("Logging");
            builder.AddConfiguration(loggingSection);
            builder.AddConsole();
            builder.AddDebug();
        });
        return (parsed, factory);
    }
}
