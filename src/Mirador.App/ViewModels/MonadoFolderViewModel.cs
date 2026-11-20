using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Monado;

namespace Mirador.App.ViewModels;

/// <summary>A copy of Monado's folder in the list, with when it was taken.</summary>
public sealed record FolderBackupItem(FolderBackup Backup)
{
    public string Text => Backup.Taken.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    /// <summary>The name an export of it gets: <c>monado-folder-20261104-120000</c>.</summary>
    public string ExportName => "monado-folder-" + Backup.Taken.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}

/// <summary>
/// App settings' "Monado folder" section: Mirador's copies of Monado's folder, with Restore, Export and Import. Restore
/// and Import go through the session, so that a Start waits until the folder is back in place.
/// </summary>
public sealed partial class MonadoFolderViewModel : ObservableObject
{
    private readonly AppHost host;

    public MonadoFolderViewModel(AppHost host)
    {
        this.host = host;
        Refresh();
    }

    public ObservableCollection<FolderBackupItem> Backups { get; } = [];

    /// <summary>The section's description: what is copied and when.</summary>
    public string Description =>
        $"Copies of {MonadoFolderBackups.DefaultMonadoDir}.";

    /// <summary>Whether the list says there are no copies yet.</summary>
    [ObservableProperty]
    public partial bool HasNoBackups { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity Severity { get; set; }

    public bool HasMessage => Message is not null;

    public void DismissMessage() => Message = null;

    /// <summary>Puts a copy back (the page asked first).</summary>
    public Task RestoreAsync(FolderBackupItem item) => DoWhileStoppedAsync(() =>
    {
        host.FolderBackups.Restore(item.Backup);
        return $"Monado's folder from {item.Text} is back. The folder it replaced is kept in the list.";
    });

    /// <summary>Writes a copy to <paramref name="zipPath"/> (the page picked it).</summary>
    public Task ExportAsync(FolderBackupItem item, string zipPath) => DoAsync(() => Task.Run(() =>
    {
        host.FolderBackups.Export(item.Backup, zipPath);
        return $"Saved {zipPath}.";
    }));

    /// <summary>Takes in an export from <paramref name="zipPath"/> (the page picked it).</summary>
    public Task ImportAsync(string zipPath) => DoWhileStoppedAsync(() =>
    {
        host.FolderBackups.Import(zipPath);
        return "Monado's folder from the export is in. The folder it replaced is kept in the list.";
    });

    /// <summary>Lists the copies again.</summary>
    public void Refresh()
    {
        IReadOnlyList<FolderBackup> backups;
        try
        {
            backups = host.FolderBackups.Backups();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            backups = [];
        }

        Backups.Clear();
        foreach (var backup in backups)
        {
            Backups.Add(new FolderBackupItem(backup));
        }

        HasNoBackups = Backups.Count == 0;
    }

    /// <summary>Runs <paramref name="action"/> while Monado is stopped; no module's work, so nothing is saved for recovery.</summary>
    private Task DoWhileStoppedAsync(Func<string> action) =>
        DoAsync(() => host.Session.RunWhileStoppedAsync("", (_, _) => Task.Run(action), CancellationToken.None));

    private async Task DoAsync(Func<Task<string>> action)
    {
        try
        {
            Message = await action();
            Severity = InfoBarSeverity.Success;
        }
        catch (MiradorException e)
        {
            Message = e.Message;
            Severity = InfoBarSeverity.Error;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't do that ({e.Message.Trim().TrimEnd('.')}).";
            Severity = InfoBarSeverity.Error;
        }

        Refresh();
    }
}
