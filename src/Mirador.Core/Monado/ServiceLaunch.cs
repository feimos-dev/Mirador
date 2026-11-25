using System.Text.RegularExpressions;
using Mirador.Core.Modules;

namespace Mirador.Core.Monado;

public static partial class ServiceLaunch
{
    public const string ConfigureOption = "-DXRT_SERVICE_EXECUTABLE=" + StandIn.ExecutableName;

    public static bool StartsStandIn(string? value) =>
        string.Equals(value?.Trim(), StandIn.ExecutableName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "monado-service", StringComparison.OrdinalIgnoreCase);

    public static StatusLine? Check(string buildFolder)
    {
        string[] lines;
        try
        {
            var cache = Path.Combine(buildFolder, "CMakeCache.txt");
            if (!File.Exists(cache))
            {
                return null;
            }

            lines = File.ReadAllLines(cache);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var value = lines.Select(line => CacheEntry().Match(line)).FirstOrDefault(m => m.Success)?.Groups["value"].Value.Trim();
        return StartsStandIn(value)
            ? null
            : new StatusLine(
                "standin",
                "Games",
                StatusLevel.Warning,
                $"Games can't start Monado from this build: it was configured without {ConfigureOption}. Configure and build it again with it, or press Start before starting a game.");
    }

    [GeneratedRegex("^XRT_SERVICE_EXECUTABLE:[A-Z]+=(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheEntry();
}
