using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Diagnostics;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;

namespace Mirador.App.ViewModels;

public sealed partial class VerifyViewModel(AppHost host, Microsoft.UI.Dispatching.DispatcherQueue ui) : ObservableObject
{
    private readonly object logGate = new();
    private Profile? profile;
    private MonadoInstall? install;
    private string? key;
    private CancellationTokenSource? running;
    private TextLog? log;

    public event Action? Changed;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    public partial bool MonadoRuns { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    public partial bool Unsaved { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool PointsToDiagnostics { get; set; }

    public string? ElevationNote { get; private set; }

    public string? ProfileId => profile?.Id;

    public string? LastLogFile { get; private set; }

    public void Attach() => host.Builds.Changed += OnBuildChanged;

    public void Detach() => host.Builds.Changed -= OnBuildChanged;

    private void OnBuildChanged(string profileId)
    {
        if (profileId == profile?.Id && !IsBusy)
        {
            ui.TryEnqueue(() => Show(profile));
        }
    }

    public void Show(Profile? saved)
    {
        profile = saved;
        install = null;
        key = null;
        if (saved is not null)
        {
            try
            {
                install = host.Locator.Locate(saved);
                key = VerifyBuild.KeyFor(install);
            }
            catch (MiradorException)
            {
            }
        }

        IsVisible = saved is not null;
        Refresh();
        VerifyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        if (profile is null)
        {
            return;
        }

        if (host.Builds.AnyRunning)
        {
            Status = "A Monado build is running: verify when it is done.";
            return;
        }

        Show(profile);
        if (install is null || key is null)
        {
            return;
        }

        var (target, targetKey, targetProfile) = (install, key, profile);
        PruneRunLogs();
        var logFile = Path.Combine(host.Paths.LogsDir, $"{DiagnosticsZip.VerifyLogPrefix}{DateTime.Now:yyyyMMdd-HHmmss}.log");
        IsBusy = true;
        host.Verifying = true;
        PointsToDiagnostics = false;
        Status = "Verifying (about a minute)…";
        running = new CancellationTokenSource();
        lock (logGate)
        {
            log = new TextLog(logFile);
        }

        try
        {
            Add($"Verify build for Windows: {targetProfile.Name}, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
            host.Session.PlaceStandIn(targetProfile);
            var result = await Task.Run(() => host.Session.RunWhileStoppedAsync(
                "",
                (_, ct) => host.Verify.RunAsync(target, targetKey, new VerifyServices(host, targetProfile, Add), Add, ct),
                running.Token));
            (result with { LogFile = logFile }).Save(host.VerifyResultFile(targetProfile.Id));
        }
        catch (OperationCanceledException)
        {
            Add("Cancelled.");
            Status = "Cancelled: Monado was stopped again.";
            LastLogFile = logFile;
            return;
        }
        catch (MiradorException e)
        {
            Add(e.Message);
            Status = e.Message;
            LastLogFile = logFile;
            PointsToDiagnostics = true;
            return;
        }
        catch (Exception e)
        {
            AppLog.Write("Verify failed", e);
            Add($"Verify stopped: {e.Message}");
            Status = $"Verify stopped: {e.Message} Diagnostics has the details.";
            LastLogFile = logFile;
            PointsToDiagnostics = true;
            return;
        }
        finally
        {
            lock (logGate)
            {
                log?.Dispose();
                log = null;
            }

            running.Dispose();
            running = null;
            host.Verifying = false;
            IsBusy = false;
        }

        Refresh();
        Changed?.Invoke();
    }

    private bool CanVerify() => install is not null && !IsBusy && !MonadoRuns && !Unsaved;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => running?.Cancel();

    private void Refresh()
    {
        ElevationNote = null;
        PointsToDiagnostics = false;
        LastLogFile = null;
        if (profile is null)
        {
            return;
        }

        var result = VerifyResult.Load(host.VerifyResultFile(profile.Id));
        LastLogFile = result?.LogFile;
        if (install is null)
        {
            Status = "This profile has no Monado yet.";
            return;
        }

        if (result is null || result.Key != key)
        {
            Status = "Not verified yet for this build.";
            return;
        }

        Status = result.Verdict(result.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        PointsToDiagnostics = result.Steps.Any(s => s.Outcome != VerifyOutcome.Passed);
        ElevationNote = result.HasElevationFix switch
        {
            true => "Verify: games running as you connect to this build running as administrator (it has the elevation fix).",
            false => "Verify: games running as you couldn't connect to this build running as administrator (no elevation fix?).",
            null => null,
        };
    }

    private void Add(string line)
    {
        AppLog.Write("Verify: " + line);
        lock (logGate)
        {
            try
            {
                log?.Write(line);
            }
            catch (IOException)
            {
            }
        }
    }

    private void PruneRunLogs()
    {
        try
        {
            var inUse = host.Profiles.Load().Profiles.Select(p => VerifyResult.Load(host.VerifyResultFile(p.Id))?.LogFile).ToList();
            VerifyRunLogs.Prune(host.Paths.LogsDir, inUse);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't tidy Verify's run logs", e);
        }
    }
}
