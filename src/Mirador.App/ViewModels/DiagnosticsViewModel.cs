using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Diagnostics;
using Mirador.Core.Modules;
using Mirador.Core.OpenVr;
using Mirador.Core.Profiles;
using Mirador.Core.Session;

namespace Mirador.App.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    public const int MaxLines = 2000;

    private readonly AppHost host;
    private readonly DispatcherQueue ui;
    private readonly object gate = new();
    private readonly Queue<string> monado = new();
    private readonly Queue<string> mirador = new();
    private readonly DispatcherQueueTimer flush;
    private bool monadoChanged;
    private bool miradorChanged;

    public DiagnosticsViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        this.ui = ui;
        flush = ui.CreateTimer();
        flush.Interval = TimeSpan.FromMilliseconds(250);
        flush.Tick += (_, _) => Flush();
    }

    public event Action<string?, string?>? LogsChanged;

    [ObservableProperty]
    public partial string Versions { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDisplayCommand), nameof(FixDirectModeCommand))]
    public partial bool CanCheckDisplay { get; set; }

    [ObservableProperty]
    public partial bool UsesDirectMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckResult))]
    public partial string? CheckResult { get; set; }

    [ObservableProperty]
    public partial bool CheckPassed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaved))]
    public partial string? Saved { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDisplayCommand), nameof(FixDirectModeCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasVerify { get; set; }

    [ObservableProperty]
    public partial string VerifySummary { get; set; } = "";

    [ObservableProperty]
    public partial string VerifySteps { get; set; } = "";

    [ObservableProperty]
    public partial string VerifyLog { get; set; } = "";

    public bool HasCheckResult => CheckResult is not null;

    public bool HasSaved => Saved is not null;

    public bool HasMessage => Message is not null;

    public void Attach()
    {
        host.Session.LogLine += OnMonadoLine;
        host.Session.Changed += OnSessionChanged;
        AppLog.LineWritten += OnMiradorLine;

        var monadoLines = ReadTail(DiagnosticsZip.NewestMonadoLog(host.Paths.LogsDir));
        var miradorLines = ReadTail(host.Paths.AppLogFile);
        lock (gate)
        {
            monado.Clear();
            foreach (var line in monadoLines)
            {
                Add(monado, line);
            }

            mirador.Clear();
            foreach (var line in miradorLines)
            {
                Add(mirador, line);
            }

            monadoChanged = miradorChanged = true;
        }

        flush.Start();
        Flush();
        Refresh();
    }

    public void Detach()
    {
        host.Session.LogLine -= OnMonadoLine;
        host.Session.Changed -= OnSessionChanged;
        AppLog.LineWritten -= OnMiradorLine;
        flush.Stop();
    }

    public void DismissMessage() => Message = null;

    public void DismissCheck() => CheckResult = null;

    public void DismissSaved() => Saved = null;

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(host.Paths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{host.Paths.LogsDir}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Message = $"Mirador couldn't open {host.Paths.LogsDir} ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task CheckDisplayAsync()
    {
        if (Profile() is not { } profile)
        {
            return;
        }

        IsBusy = true;
        CheckResult = null;
        try
        {
            var check = await host.WithDisplayFreeAsync(profile, (directMode, ct) => directMode.CheckDisplayAsync(host.Context, ct));
            CheckPassed = check.Level == StatusLevel.Ok;
            CheckResult = check.Text;
        }
        catch (MiradorException e)
        {
            CheckPassed = false;
            CheckResult = e.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private bool CanRunCheck() => CanCheckDisplay && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task FixDirectModeAsync()
    {
        if (Profile() is not { } profile)
        {
            return;
        }

        IsBusy = true;
        CheckResult = null;
        try
        {
            var problem = await host.WithDisplayFreeAsync(profile, (directMode, ct) => directMode.FixAsync(host.Context, ct));
            CheckPassed = problem is null;
            CheckResult = problem is null ? "Direct mode is on for the headset." : Sentence(problem);
            host.NotifyDevicesChanged();
        }
        catch (MiradorException e)
        {
            CheckPassed = false;
            CheckResult = e.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        if (Profile() is not { } profile)
        {
            return;
        }

        var path = await Pickers.SaveFileAsync($"mirador-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}", "Zip archive", ".zip");
        if (path is null)
        {
            return;
        }

        Saved = null;
        Message = null;
        try
        {
            var (module, directMode) = host.PartsOf(profile);
            var versions = AppInfo.Versions(profile, host.AppDir);
            var environment = host.Session.LastMiradorEnvironment;
            var openVr = new OpenVrFiles(OpenVrPaths.DefaultFile, OpenCompositeInstall.LogFile);
            await Task.Run(() => DiagnosticsZip.Collect(host.Paths, profile, environment, versions, module, host.Context, openVr, directMode).Save(path));
            Saved = $"Saved {path}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't save {path} ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    private static string Sentence(string s) => s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? s : s + ".";

    private static List<string> ReadTail(string? path)
    {
        var lines = new Queue<string>();
        if (path is null)
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > MaxLines)
                {
                    lines.Dequeue();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return [.. lines];
    }

    private Profile? Profile()
    {
        try
        {
            return host.Profiles.Load().Selected;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).";
            return null;
        }
    }

    private void Refresh()
    {
        var profile = Profile();
        UsesDirectMode = profile is not null && host.PartsOf(profile).DirectMode is not null;
        CanCheckDisplay = UsesDirectMode && host.Session.State == SessionState.Stopped;
        Versions = profile is null ? $"Mirador {AppInfo.Version}" : AppInfo.Versions(profile, host.AppDir).TrimEnd();
        RefreshVerify();
    }

    private string? wantedVerifyProfile;
    private string? wantedVerifyLog;

    public void ShowVerify(string? profileId, string? logFile)
    {
        wantedVerifyProfile = profileId;
        wantedVerifyLog = logFile;
    }

    private void RefreshVerify()
    {
        IReadOnlyList<Profile> profiles;
        try
        {
            profiles = host.Profiles.Load().Profiles;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            profiles = [];
        }

        var shown = VerifyResult.ForDiagnostics(
            profiles.Select(p => (p.Id, VerifyResult.Load(host.VerifyResultFile(p.Id)))), wantedVerifyProfile);
        HasVerify = shown is not null;
        if (shown is { } found)
        {
            var name = profiles.FirstOrDefault(p => p.Id == found.ProfileId)?.Name ?? found.ProfileId;
            var result = found.Result;
            VerifySummary = $"{name}, {result.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}: {result.Summary()}";
            VerifySteps = string.Join(Environment.NewLine, result.Steps.Select(s => $"{s.Outcome switch
            {
                VerifyOutcome.Passed => "Passed",
                VerifyOutcome.Failed => "Failed",
                _ => "Skipped",
            }}: {s.Name}. {s.Detail}"));
        }

        var logFile = new[] { wantedVerifyLog, shown?.Result.LogFile }.FirstOrDefault(f => f is not null && File.Exists(f))
            ?? DiagnosticsZip.NewestVerifyLog(host.Paths.LogsDir);
        var log = ReadTail(logFile);
        VerifyLog = log.Count > 0 ? string.Join(Environment.NewLine, log) : "No Verify run yet. Verify build for Windows is on the Profiles page.";
    }

    private void OnSessionChanged() => ui.TryEnqueue(Refresh);

    private void OnMonadoLine(string line)
    {
        lock (gate)
        {
            Add(monado, line);
            monadoChanged = true;
        }
    }

    private void OnMiradorLine(string line)
    {
        lock (gate)
        {
            Add(mirador, line);
            miradorChanged = true;
        }
    }

    private static void Add(Queue<string> lines, string line)
    {
        lines.Enqueue(line);
        while (lines.Count > MaxLines)
        {
            lines.Dequeue();
        }
    }

    private void Flush()
    {
        string? monadoText = null, miradorText = null;
        lock (gate)
        {
            if (monadoChanged)
            {
                monadoText = string.Join('\n', monado);
                monadoChanged = false;
            }

            if (miradorChanged)
            {
                miradorText = string.Join('\n', mirador);
                miradorChanged = false;
            }
        }

        if (monadoText is not null || miradorText is not null)
        {
            LogsChanged?.Invoke(monadoText, miradorText);
        }
    }
}
