using System.Reflection;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// Build-time values that do not belong in the assembly version. <c>INDEX_VERSION</c> in the repo
/// root <c>.env</c> is injected as <c>AssemblyMetadata</c> by the csproj, so the index structure
/// version is bumped in exactly one place — the same file that holds the app version.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// On-disk index structure version. The app stamps it into every index it writes and refuses to
    /// load an index stamped with anything else (including an index from before this existed, which
    /// has no stamp at all), which makes the rebuild automatic after an upgrade that changed the
    /// row format.
    /// </summary>
    public static int IndexVersion { get; } = Read("IndexVersion");

    private static int Read(string key)
    {
        foreach (var attribute in typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (string.Equals(attribute.Key, key, StringComparison.Ordinal)
                && int.TryParse(attribute.Value, out var value))
                return value;
        return 0;
    }
}
