using System.Collections.Frozen;
using System.Reflection;

namespace CsSsg.Src.Program;

internal interface IFeatureGater
{
    void Gate(string mode, Action func);    
}

/// <summary>
/// Feature flags handler.
/// </summary>
public class Features : IFeatureGater
{
    /// <summary>
    /// This feature flag enables features corresponding to HTML (form) API.
    /// </summary>
    [FeatureFlag] public const string HtmlApi = "htmlapi";
    /// <summary>
    /// This feature flag enables features corresponding to JSON API.
    /// </summary>
    [FeatureFlag] public const string JsonApi = "jsonapi";
    /// <summary>
    /// This feature flags enables using the database for media storage.
    /// </summary>
    [FeatureFlag] public const string DbMediaStorage = "dbmediastorage";

    // use reflection to collect the [FeatureFlag] marked strings to create a lookup set
    private static readonly FrozenSet<string> FlagValues =
        typeof(Features).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(fi => fi.GetCustomAttribute<FeatureFlag>() != null)
            .Select(fi =>
                fi.GetValue(null) as string
                    ?? throw new InvalidOperationException("unexpected: feature flag with null value")
            )
            .ToFrozenSet();

    /// <summary>
    /// Parses a comma separated list of features to enable, creating a new <see cref="Features"/> to store them.
    /// <br />
    /// Unused parameters are printed to standard error.
    /// </summary>
    internal static Features ParseFeatureFlagsString(string flagsStr)
    {
        var unused = new HashSet<string>();
        var flags = new HashSet<string>();
        foreach (var flag in 
                 flagsStr
                     .Split(',')
                     .Select(s => s.Trim())
                     .Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (FlagValues.Contains(flag))
                flags.Add(flag);
            else
                unused.Add(flag);
        }

        if (unused.Count > 0)
        {
            // this function is invoked during building so we don't have a ready logger yet
            Console.Error.WriteLine($"unused features: {string.Join(",", unused)}");
        }

        return new Features(flags);
    }

    private Features(IReadOnlySet<string> flags)
    {
        _flags = flags;
    }

    private readonly IReadOnlySet<string> _flags;
    
    /// <summary>
    /// Call some conditional initialization function based on whether a feature is enabled.
    /// </summary>
    /// <param name="mode">The feature flag</param>
    /// <param name="func">The function to conditionally call</param>
    public void Gate(string mode, Action func)
    {
        if (_flags.Contains(mode))
            func();
    }
}

/// <summary>
/// Environment mode handler.
/// </summary>
public class EnvironmentFeature : IFeatureGater
{
    public static readonly string Dev = Environments.Development;
    public static readonly string Prod = Environments.Production;
    public static readonly string Staging = Environments.Staging;
    
    internal static EnvironmentFeature FromEnvironment(IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
            return new EnvironmentFeature(Dev);
        if (env.IsProduction())
            return new EnvironmentFeature(Prod);
        if (env.IsStaging())
            return new EnvironmentFeature(Staging);
        throw new InvalidOperationException($"unexpected environment state {env}");
    }

    private EnvironmentFeature(string envMode)
    {
        _envMode = envMode;
    }

    private readonly string _envMode;

    /// <summary>
    /// Call some conditional initialization function based on whether a feature is enabled.
    /// </summary>
    /// <param name="mode">The environment mode</param>
    /// <param name="func">The function to conditionally call</param>
    public void Gate(string mode, Action func)
    {
        if (_envMode == mode)
            func();
    }
}

[AttributeUsage(AttributeTargets.Field)]
file class FeatureFlag : Attribute;