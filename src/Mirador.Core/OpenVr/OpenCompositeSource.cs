using System.Text.RegularExpressions;

namespace Mirador.Core.OpenVr;

public sealed partial record OpenCompositeSource(string? Version, string? Commit)
{
    public static OpenCompositeSource? Read(string sourceTxt)
    {
        var first = sourceTxt.Split('\n', 2)[0].Trim();

        var release = ReleaseLine().Match(first);
        if (release.Success)
        {
            return new OpenCompositeSource(release.Groups[1].Value, release.Groups[2].Value);
        }

        return CommitLine().IsMatch(first) ? new OpenCompositeSource(null, first) : null;
    }

    public string Describe()
    {
        var shortCommit = Commit is { Length: > 7 } ? Commit[..7] : Commit;
        return (Version, shortCommit) switch
        {
            (null, null) => "",
            (null, _) => shortCommit!,
            (_, null) => Version,
            _ => $"{Version} ({shortCommit})",
        };
    }

    public static int Compare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return (a is null ? 0 : 1) - (b is null ? 0 : 1);
        }

        var left = Numbers(a);
        var right = Numbers(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static long[] Numbers(string version) =>
        [.. version.Split('.').Select(part => long.TryParse(part, out var n) ? n : 0)];

    [GeneratedRegex(@"^(?:OpenLamina|OpenComposite) (\d+(?:\.\d+)+), commit ([0-9a-f]{40})$")]
    private static partial Regex ReleaseLine();

    [GeneratedRegex(@"^[0-9a-f]{40}$")]
    private static partial Regex CommitLine();
}
