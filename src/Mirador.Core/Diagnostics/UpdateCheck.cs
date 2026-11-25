using System.Net.Http.Headers;
using System.Text.Json;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Diagnostics;

public sealed record Release(string Tag, string Url, string? InstallerUrl, string? InstallerSha256, string? SumsUrl = null, string? SignatureUrl = null);

public static class UpdateCheck
{
    public const string MiradorRepository = "feimos-dev/mirador";

    public const string OpenCompositeRepository = "feimos-dev/OpenLamina";

    internal const string Installer = "OpenLamina-Windows-Installer.exe";

    private const string DigestPrefix = "sha256:";

    public static Release? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
                && root.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
                && !(root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                && !(root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True))
            {
                var (installer, sha) = FindInstaller(root);
                return new Release(
                    tag.GetString()!, url.GetString()!, installer, sha,
                    FindAsset(root, SignedSums.SumsName), FindAsset(root, SignedSums.SignatureName));
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    public static bool IsNewer(string current, string tag) =>
        Parse(current) is { } mine && Parse(tag) is { } theirs && theirs > mine;

    public static async Task<(bool Answered, Release? Latest)> LatestAsync(HttpClient http, string repository, string userAgentVersion, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mirador", Parse(userAgentVersion)?.ToString() ?? "0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (true, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            return (true, ParseLatest(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return (false, null);
        }
    }

    private static (string? Url, string? Sha) FindInstaller(JsonElement release)
    {
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && Text(asset, "name") == Installer
                    && Text(asset, "browser_download_url") is { Length: > 0 } url
                    && Text(asset, "digest") is { } digest
                    && digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)
                    && digest.Length > DigestPrefix.Length)
                {
                    return (url, digest[DigestPrefix.Length..].ToLowerInvariant());
                }
            }
        }

        return (null, null);
    }

    private static string? FindAsset(JsonElement release, string name)
    {
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && Text(asset, "name") == name
                    && Text(asset, "browser_download_url") is { Length: > 0 } url)
                {
                    return url;
                }
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Version? Parse(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
        {
            t = t[1..];
        }

        var plus = t.IndexOfAny(['+', '-']);
        if (plus >= 0)
        {
            t = t[..plus];
        }

        if (!t.Contains('.'))
        {
            t += ".0";
        }

        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }
}
