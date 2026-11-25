using System.Text.RegularExpressions;
using Mirador.Ipc;

namespace Mirador.Helper;

public static partial class PimaxEyeBlob
{
    public static string? Parse(string commandLine)
    {
        foreach (Match match in ConfigFile().Matches(commandLine))
        {
            var variant = match.Groups["variant"].Value;
            if (PimaxNames.EyeBlobVariants.Contains(variant))
            {
                return variant;
            }
        }

        return null;
    }

    [GeneratedRegex(@"config_XR5PIMAXDA_(?<variant>[A-Za-z0-9_]+)\.bin", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigFile();
}
