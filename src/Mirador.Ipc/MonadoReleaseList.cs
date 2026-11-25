using System.Net.Http.Headers;
using System.Text.Json;

namespace Mirador.Ipc;

public sealed record MonadoRelease(string Version, string ZipUrl, string ZipSha256, DateTimeOffset PublishedAt);

public static class MonadoReleaseList
{
    public const string Repository = "feimos-dev/Mirador";

    public const string ApiUrl = $"https://api.github.com/repos/{Repository}/releases?per_page=100";

    private const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";
    private const string DigestPrefix = "sha256:";

    public static IReadOnlyList<MonadoRelease> Parse(string json)
    {
        var found = new List<MonadoRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || Text(release, "tag_name") is not { } version
                    || !MiradorMonado.IsVersion(version)
                    || !Flag(release, "prerelease")
                    || Flag(release, "draft")
                    || !release.TryGetProperty("assets", out var assets)
                    || assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var published = DateTimeOffset.TryParse(Text(release, "published_at"), out var at) ? at : DateTimeOffset.MinValue;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind == JsonValueKind.Object
                        && Text(asset, "name") == version + ".zip"
                        && Text(asset, "browser_download_url") is { } url
                        && url == $"{DownloadPrefix}{version}/{version}.zip"
                        && Text(asset, "digest") is { } digest
                        && digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)
                        && digest.Length == DigestPrefix.Length + 64
                        && digest[DigestPrefix.Length..].All(Uri.IsHexDigit))
                    {
                        found.Add(new MonadoRelease(version, url, digest[DigestPrefix.Length..].ToLowerInvariant(), published));
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [.. found.OrderByDescending(r => r.PublishedAt).ThenByDescending(r => r.Version, StringComparer.Ordinal)];
    }

    public static async Task<IReadOnlyList<MonadoRelease>?> FetchAsync(HttpClient http, string userAgentVersion, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mirador", userAgentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
