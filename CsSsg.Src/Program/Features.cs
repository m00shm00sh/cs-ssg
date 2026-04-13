using System.Collections.Frozen;
using System.Reflection;

namespace CsSsg.Src.Program;

/// <summary>
/// Feature flags handler.
/// </summary>
public class Features
{
    /// <summary>
    /// This feature flag enables features corresponding to HTML (form) API.
    /// </summary>
    [FeatureFlag] public const string HtmlApi = "htmlapi";
    /// <summary>
    /// This feature flag enables features corresponding to JSON API.
    /// </summary>
    [FeatureFlag] public const string JsonApi = "jwtapi";

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
    /// <param name="flag">The feature flag</param>
    /// <param name="func">The function to conditionally call</param>
    internal void Gate(string flag, Action func)
    {
        if (_flags.Contains(flag))
            func();
    }
}

[AttributeUsage(AttributeTargets.Field)]
file class FeatureFlag : Attribute;