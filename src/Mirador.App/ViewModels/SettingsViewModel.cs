using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.SignIn;
using Mirador.Ipc;

namespace Mirador.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppHost host;
    private readonly SignInStartup signIn = new(new RegistryRunKeys(), Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"));
    private readonly DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();
    private bool loading;

    public SettingsViewModel(AppHost host)
    {
        this.host = host;
        App.Current.Updates.Changed += OnUpdatesChanged;
        ShowReport();
        loading = true;
        var settings = Read(host, out var problem);
        Problem = problem;
        CloseToTray = settings.CloseToTray;
        MinimizeToTray = settings.MinimizeToTray;
        CheckForUpdates = settings.CheckForUpdates;
        StartMiradorForGames = settings.StartMiradorForGames;
        StartMinimized = settings.StartMinimized;
        HomeEnvironment = settings.HomeEnvironment;
        Dashboard = settings.Dashboard;
        try
        {
            StartAtSignIn = signIn.IsOn;
        }
        catch (MiradorException e)
        {
            Problem = e.Message;
        }

        loading = false;
    }

    [ObservableProperty]
    public partial bool StartAtSignIn { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool StartMiradorForGames { get; set; }

    [ObservableProperty]
    public partial bool HomeEnvironment { get; set; }

    [ObservableProperty]
    public partial bool Dashboard { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    public string Version => $"Mirador {AppInfo.Version}";

    [ObservableProperty]
    public partial string UpdatesText { get; set; } = "Not checked yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMiradorRelease))]
    public partial Uri? MiradorReleaseUrl { get; set; }

    public bool HasMiradorRelease => MiradorReleaseUrl is not null;

    [ObservableProperty]
    public partial bool CanUpdateOpenComposite { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial bool Checking { get; set; }

    public string BundledMonadoVersion =>
        MiradorMonado.Read(host.Locator.MiradorRoot).Current is { } current ? $"Bundled Monado: {current}" : "Bundled Monado: not installed yet";

    public string OpenCompositeDescription =>
        host.OpenComposite.Source is { } source
            ? source.Describe()
            : host.OpenComposite.IsInstalled ? "Version unknown"
            : "Not installed";

    internal static string DescribeLastCheck(DateTime last, DateTime now)
    {
        var time = last.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        var days = (now.Date - last.Date).Days;
        return days switch
        {
            0 => $"today at {time}",
            1 => $"yesterday at {time}",
            _ => $"on {last.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)} at {time}",
        };
    }

    private void OpenCompositeChanged()
    {
        OnPropertyChanged(nameof(OpenCompositeDescription));
    }

    public void Dispose() => App.Current.Updates.Changed -= OnUpdatesChanged;

    internal static AppSettings Read(AppHost host, out string? problem)
    {
        try
        {
            problem = null;
            return host.Settings.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"Mirador couldn't read its settings ({e.Message.Trim().TrimEnd('.')}).";
            return new AppSettings();
        }
    }

    internal static string? Write(AppHost host, Func<AppSettings, AppSettings> change)
    {
        try
        {
            host.Settings.Save(change(host.Settings.Load()));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Mirador couldn't save its settings ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        Checking = true;
        outcome = null;
        try
        {
            await App.Current.Updates.CheckAsync();
            Problem = App.Current.Updates.RecordProblem;
        }
        catch (Exception e)
        {
            AppLog.Write("The update check failed", e);
            UpdatesText = $"The check failed ({e.Message.Trim().TrimEnd('.')}).";
        }
        finally
        {
            Checking = false;
        }
    }

    private bool CanCheckNow() => !Checking;

    private string? outcome;

    private async Task ShowOutcome(Task<string> operation)
    {
        outcome = null;
        outcome = await operation;
        ShowReport();
    }

    private void OnUpdatesChanged() => ui.TryEnqueue(ShowReport);

    private void ShowReport()
    {
        var updates = App.Current.Updates;
        if (updates.Last is not { } report)
        {
            if (Read(host, out _).LastUpdateCheck is { } last)
            {
                UpdatesText = $"Last checked {DescribeLastCheck(last.LocalDateTime, DateTime.Now)}.";
            }

            return;
        }

        UpdatesText = outcome is null ? report.Describe() : $"{outcome} {report.Describe()}";
        MiradorReleaseUrl = report.NewerMirador is { } m && Uri.TryCreate(m.Url, UriKind.Absolute, out var url) ? url : null;
        CanUpdateOpenComposite = report.NewerOpenComposite is { InstallerUrl: not null } && updates.CanUpdateOpenComposite;
    }

    [RelayCommand]
    private async Task UpdateOpenCompositeAsync()
    {
        if (App.Current.Updates.Last?.NewerOpenComposite is not { } release)
        {
            return;
        }

        try
        {
            Problem = null;
            await ShowOutcome(App.Current.Updates.UpdateOpenCompositeAsync(release));
        }
        catch (Exception e)
        {
            AppLog.Write("Updating OpenLamina failed", e);
            Problem = $"Updating OpenLamina failed: {e.Message.Trim()}";
        }

        OpenCompositeChanged();
    }

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnHomeEnvironmentChanged(bool value)
    {
        Save(s => s with { HomeEnvironment = value });
        SwitchesChanged();
    }

    partial void OnDashboardChanged(bool value)
    {
        Save(s => s with { Dashboard = value });
        SwitchesChanged();
    }

    private void SwitchesChanged()
    {
        if (!loading)
        {
            var xr = host.Xr;
            _ = Task.Run(xr.SwitchesChanged);
        }
    }

    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s with { MinimizeToTray = value });

    partial void OnStartAtSignInChanged(bool value)
    {
        if (loading)
        {
            return;
        }

        try
        {
            if (value)
            {
                signIn.TurnOn();
            }
            else
            {
                signIn.TurnOff();
            }

            Problem = null;
        }
        catch (MiradorException e)
        {
            Problem = e.Message;

            loading = true;
            try
            {
                StartAtSignIn = !value;
            }
            finally
            {
                loading = false;
            }
        }
    }

    partial void OnStartMinimizedChanged(bool value) => Save(s => s with { StartMinimized = value });

    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s with { CheckForUpdates = value });

    partial void OnStartMiradorForGamesChanged(bool value) => Save(s => s with { StartMiradorForGames = value });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!loading)
        {
            Problem = Write(host, change);
        }
    }
}
