using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mirador.App.Services;

namespace Mirador.App.ViewModels;

public sealed record InstalledItem(string Name, string Detail);

public sealed record InstalledPart(string Name, string Detail, string LinkText, Uri Link);

public sealed partial class InstalledViewModel(AppHost host) : ObservableObject
{
    public ObservableCollection<InstalledPart> Parts { get; } = [];

    public ObservableCollection<InstalledItem> BuildTools { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public async Task RefreshAsync()
    {
        var versions = Ipc.MiradorMonado.Read(host.Locator.MiradorRoot);
        Parts.Clear();
        Parts.Add(new InstalledPart(
            "Mirador",
            AppInfo.Version,
            "github.com/feimos-dev/Mirador",
            new Uri("https://github.com/feimos-dev/Mirador")));
        Parts.Add(new InstalledPart(
            "Bundled Monado",
            versions.Current is not { } current ? "Not installed: the Profiles page installs it."
            : versions.Previous is { } previous ? $"{current} (can roll back to {previous})"
            : current,
            "gitlab.freedesktop.org/monado/monado",
            new Uri("https://gitlab.freedesktop.org/monado/monado")));
        Parts.Add(new InstalledPart(
            "OpenLamina",
            host.OpenComposite.Source is { } source && (source.Version ?? source.Describe()) is { Length: > 0 } described ? described
            : host.OpenComposite.IsInstalled ? "Installed, version unknown"
            : "Not installed: Home's OpenVR card gets it.",
            "github.com/feimos-dev/OpenLamina",
            new Uri("https://github.com/feimos-dev/OpenLamina")));

        IsLoading = true;
        try
        {
            var report = await Task.Run(() => host.BuildToolsSetup.CheckAsync(CancellationToken.None));
            BuildTools.Clear();
            foreach (var part in report.Parts)
            {
                BuildTools.Add(new InstalledItem(part.Name, part.Detail));
            }

            Problem = null;
        }
        catch (Exception e)
        {
            if (e is not Core.MiradorException)
            {
                AppLog.Write("Reading the build tools failed", e);
            }

            Problem = $"Mirador couldn't read the build tools ({e.Message.Trim().TrimEnd('.')}).";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
