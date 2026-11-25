using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Core.Build;
using Mirador.Core.Profiles;

namespace Mirador.App.ViewModels;

public sealed partial class BuildViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue ui;
    private Profile? profile;

    public BuildViewModel(AppHost host, Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        this.host = host;
        this.ui = ui;
    }

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(CancelCommand))]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string BuildLabel { get; set; } = "Build";

    [ObservableProperty]
    public partial string Log { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial bool IsApproving { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial bool Unsaved { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial bool MonadoRuns { get; set; }

    public void Attach() => host.Builds.Changed += OnChanged;

    public void Detach() => host.Builds.Changed -= OnChanged;

    public void Show(Profile? saved)
    {
        profile = saved?.Monado.Kind == SourceKind.Branch ? saved : null;
        IsVisible = profile is not null;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private void Build()
    {
        if (host.Verifying)
        {
            Status = "Verify is running. Build when it's done.";
            return;
        }

        if (profile is not null)
        {
            host.Builds.Start(profile);
        }
    }

    private bool CanBuild() => profile is not null && !IsBuilding && !IsApproving && !Unsaved && !MonadoRuns;

    [RelayCommand(CanExecute = nameof(IsBuilding))]
    private void Cancel()
    {
        if (profile is not null)
        {
            host.Builds.Cancel(profile.Id);
        }
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (profile is not null && host.Builds.LastLog(profile.Id) is { } log)
        {
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        }
    }

    private void OnChanged(string profileId)
    {
        if (profileId == profile?.Id)
        {
            ui.TryEnqueue(Refresh);
        }
    }

    private void Refresh()
    {
        if (profile is null)
        {
            return;
        }

        var run = host.Builds.RunOf(profile.Id);
        IsBuilding = run is { Outcome: null, Cancelled: false };
        IsApproving = run is { Approving: true };
        Log = run?.LastLine ?? "";
        var installDir = host.Locator.BranchInstallDir(profile.Id);
        var info = BuildInfo.Load(installDir);
        BuildLabel = info is null ? "Build" : "Pull and rebuild";
        if (IsBuilding || IsApproving)
        {
            Status = IsBuilding ? "Building…" : "Approving…";
            return;
        }

        var built = info is null
            ? "Not built yet."
            : $"Built {info.ShortCommit} on {info.BuiltAt} with {info.Compiler}. {BuildCheckResult.Load(installDir)?.Summary()}";
        Status = run switch
        {
            { Cancelled: true } => $"Cancelled. {built}",
            { Outcome: { Ok: false } failed } => $"{failed.Message} {built}",
            _ => built,
        };
    }
}
