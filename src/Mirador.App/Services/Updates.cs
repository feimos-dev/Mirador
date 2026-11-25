using Mirador.Core;
using Mirador.Core.Diagnostics;
using Mirador.Core.OpenVr;

namespace Mirador.App.Services;

public sealed record UpdateReport(
    DateTimeOffset At,
    bool Reached,
    string Mirador,
    Release? NewerMirador,
    string? OpenComposite,
    Release? NewerOpenComposite,
    string? NewerMonado = null)
{
    public string Describe()
    {
        if (!Reached)
        {
            return "Couldn't reach GitHub.";
        }

        var parts = new List<string>
        {
            NewerMirador is { } m ? $"Mirador {Plain(m.Tag)} is out." : $"Mirador {Mirador} is up to date.",
        };
        if (NewerOpenComposite is { } o)
        {
            parts.Add($"OpenLamina {Plain(o.Tag)} is out.");
        }
        else if (OpenComposite is not null)
        {
            parts.Add($"OpenLamina {OpenComposite} is up to date.");
        }

        if (NewerMonado is { } monado)
        {
            parts.Add($"The bundled Monado {monado} is out: update it on the Profiles page.");
        }

        parts.Add($"Checked at {At.ToLocalTime():HH:mm}.");
        return string.Join(' ', parts);
    }

    public static string Plain(string tag) => tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
}

public sealed class Updates(AppHost host)
{
    public UpdateReport? Last { get; private set; }

    public string? RecordProblem { get; private set; }

    public event Action? Changed;

    private OpenCompositeSource? Installed() => host.OpenComposite.Source;

    public bool CanUpdateOpenComposite => true;

    public async Task<string> UpdateOpenCompositeAsync(Release release)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mirador/{AppInfo.Version}");
        if (await host.OpenComposite.InstallAsync(http, release, host.Paths.DownloadDir, CancellationToken.None) == InstallOutcome.InUse)
        {
            throw new MiradorException("A game is using OpenLamina, so it wasn't changed. Close the game and try again.");
        }

        var message = $"OpenLamina {UpdateReport.Plain(release.Tag)} is installed.";
        AppLog.Write(message);
        await CheckAsync();
        return message;
    }

    public async Task<string> GetOpenCompositeAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var (answered, latest) = await UpdateCheck.LatestAsync(http, UpdateCheck.OpenCompositeRepository, AppInfo.Version, CancellationToken.None);
        if (!answered)
        {
            throw new MiradorException("Mirador couldn't reach GitHub. Try again later.");
        }

        return latest is null
            ? throw new MiradorException("OpenLamina has no release yet.")
            : await UpdateOpenCompositeAsync(latest);
    }

    private async Task<string?> NewerMonadoAsync(HttpClient http)
    {
        try
        {
            if (!host.Profiles.Load().Profiles.Any(p => p.Monado.Kind == Core.Profiles.SourceKind.Mirador))
            {
                return null;
            }

            var newest = (await Ipc.MonadoReleaseList.FetchAsync(http, AppInfo.Version, CancellationToken.None))?.FirstOrDefault();
            var current = Ipc.MiradorMonado.Read(host.Locator.MiradorRoot).Current;
            return newest is not null && newest.Version != current ? newest.Version : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<UpdateReport?> CheckDailyAsync()
    {
        AppSettings settings;
        try
        {
            settings = host.Settings.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't read the settings for the update check", ex);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (!settings.CheckForUpdates || (settings.LastUpdateCheck is { } last && now - last < TimeSpan.FromDays(1) && last <= now))
        {
            return null;
        }

        return await CheckAsync();
    }

    public async Task<UpdateReport> CheckAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var installed = Installed();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var (mAnswered, mLatest) = await UpdateCheck.LatestAsync(http, UpdateCheck.MiradorRepository, AppInfo.Version, CancellationToken.None);
        var (oAnswered, oLatest) = await UpdateCheck.LatestAsync(http, UpdateCheck.OpenCompositeRepository, AppInfo.Version, CancellationToken.None);

        var newerMonado = await NewerMonadoAsync(http);
        var report = new UpdateReport(
            now,
            mAnswered && oAnswered,
            AppInfo.Version,
            mLatest is { } m && UpdateCheck.IsNewer(AppInfo.Version, m.Tag) ? m : null,
            installed is null ? null : installed.Version ?? installed.Describe(),
            oLatest is { } o && installed is not null && (installed.Version is null || UpdateCheck.IsNewer(installed.Version, o.Tag)) ? o : null,
            newerMonado);

        if (report.Reached)
        {
            RecordProblem = SettingsWrite(now);
        }

        Last = report;

        if (report.NewerMirador is { } newerMirador)
        {
            AppLog.Write($"Mirador {newerMirador.Tag} is out: {newerMirador.Url}");
        }

        if (report.NewerOpenComposite is { } newerOpenComposite)
        {
            AppLog.Write($"OpenLamina {newerOpenComposite.Tag} is out: {newerOpenComposite.Url}");
        }

        if (!report.Reached)
        {
            AppLog.Write("Update check: couldn't reach GitHub");
        }

        Changed?.Invoke();
        return report;
    }

    private string? SettingsWrite(DateTimeOffset now)
    {
        try
        {
            host.Settings.Save(host.Settings.Load() with { LastUpdateCheck = now });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't record the update check", ex);
            return $"Mirador couldn't record the update check ({ex.Message.Trim().TrimEnd('.')}).";
        }
    }
}
