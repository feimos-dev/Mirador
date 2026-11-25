using Mirador.Core.Profiles;

namespace Mirador.Core.Lighthouse;

public static class LighthouseConflicts
{
    public static IReadOnlyList<string> ProcessNames { get; } = ["vrserver", "vrmonitor", "vrcompositor", "OVRLighthouseManager"];

    private const string EnableVariable = "STEAMVR_LH_ENABLE";

    private static readonly string[] TrueValues = ["1", "true", "on", "yes"];

    public static IReadOnlyList<string> Find(IReadOnlyList<ProcInfo> running) =>
        ProcessNames
            .Where(name => running.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string Message(IReadOnlyList<string> found)
    {
        if (found.Count == 0)
        {
            throw new ArgumentException("There is no program to name.", nameof(found));
        }

        return found.Count == 1
            ? $"Close {found[0]} first: it uses the Lighthouse receiver."
            : $"Close {string.Join(", ", found)} first: they use the Lighthouse receiver.";
    }

    public static bool UsesLighthouse(Profile p, bool moduleUsesLighthouse)
    {
        if (moduleUsesLighthouse)
        {
            return true;
        }

        var setting = p.ExtraEnvironment.LastOrDefault(
            e => string.Equals(e.Name, EnableVariable, StringComparison.OrdinalIgnoreCase));
        return setting is not null && TrueValues.Contains(setting.Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
