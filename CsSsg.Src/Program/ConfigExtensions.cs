namespace CsSsg.Src.Program;

internal static class ConfigExtensions
{
    extension(IConfiguration config)
    {
        /// <summary>
        /// Gets config item from envar or config, or throws exception on failure.
        /// See <see cref="GetFromEnvironmentOrConfigOrNull"/>
        /// </summary>
        public string GetFromEnvironmentOrConfig(string envName, string cfgName)
            => config.GetFromEnvironmentOrConfigOrNull(envName, cfgName)
               ?? throw new InvalidOperationException(
                   $"The environment variable {envName} does not exist and neither does the config item {cfgName}.");
        
        /// <summary>
        /// Gets config item from envar or config, or null if niether found.
        /// </summary>
        /// <param name="envName">environment variable name</param>
        /// <param name="cfgName">config item name (use ":" for section delimiter)</param>
        public string? GetFromEnvironmentOrConfigOrNull(string envName, string cfgName)
            => Environment.GetEnvironmentVariable(envName)
               ?? config[cfgName];
    }
}