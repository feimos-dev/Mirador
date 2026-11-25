using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Ipc;

namespace Mirador.App.ViewModels;

public sealed partial class MiradorMonadoViewModel(AppHost host) : ObservableObject
{
    private string? newest;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RollBackCommand), nameof(CheckCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string InstallLabel { get; set; } = "Install";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool CanInstall { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RollBackCommand))]
    public partial bool HasPrevious { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RollBackCommand))]
    public partial bool MonadoRuns { get; set; }

    public void Show(bool visible)
    {
        IsVisible = visible;
        Refresh();
        if (visible && newest is null && !IsBusy)
        {
            _ = CheckAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var releases = await MonadoReleaseList.FetchAsync(http, AppInfo.Version, CancellationToken.None);
            newest = releases?.FirstOrDefault()?.Version;
            Refresh(releases is null ? "Couldn't reach GitHub." : newest is null ? "No build is published yet." : null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private async Task InstallAsync()
    {
        if (newest is null)
        {
            return;
        }

        IsBusy = true;
        Status = $"Installing {newest}…";
        try
        {
            var reply = await host.Helper.SendAsync(new HelperRequest(HelperCommand.InstallMonado, Version: newest), CancellationToken.None);
            Refresh(reply.Ok ? null : reply.Error);
            host.NotifyProfilesChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstallNow() => CanInstall && !IsBusy && !MonadoRuns;

    [RelayCommand(CanExecute = nameof(CanRollBack))]
    private async Task RollBackAsync()
    {
        IsBusy = true;
        try
        {
            var reply = await host.Helper.SendAsync(HelperCommand.RollBackMonado, CancellationToken.None);
            Refresh(reply.Ok ? null : reply.Error);
            host.NotifyProfilesChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRollBack() => HasPrevious && !IsBusy && !MonadoRuns;

    private void Refresh(string? problem = null)
    {
        var versions = MiradorMonado.Read(host.Locator.MiradorRoot);
        HasPrevious = versions.Previous is not null;
        CanInstall = newest is not null && newest != versions.Current;
        InstallLabel = versions.Current is null ? "Install" : "Update Monado";
        var installed = versions.Current is null
            ? "Not installed yet."
            : $"Installed: {Date(versions.Current)}.";
        var latest = newest is null ? "" : newest == versions.Current ? " Up to date." : $" Newest: {Date(newest)}.";
        Status = problem is null ? installed + latest : $"{problem} {installed}";
    }

    private static string Date(string version) =>
        MiradorMonado.DateOf(version) is { } date ? date.ToString("d", System.Globalization.CultureInfo.CurrentCulture) : version;
}
