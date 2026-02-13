namespace CsSsg.Program;

internal static class ConfigExtensions
{
    extension(IConfiguration config)
    {
        public string GetFromEnvironmentOrConfig(string envName, string cfgName)
            => config.GetFromEnvironmentOrConfigOrNull(envName, cfgName)
               ?? throw new ArgumentNullException(null,
                   $"The environment variable {envName} does not exist and neither does the config item {cfgName}.");
        
        public string? GetFromEnvironmentOrConfigOrNull(string envName, string cfgName)
            => Environment.GetEnvironmentVariable(envName)
               ?? config[cfgName];
    }
}