using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Current.Host);
        Folder = new MonadoFolderViewModel(App.Current.Host);
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public SettingsViewModel ViewModel { get; }

    public MonadoFolderViewModel Folder { get; }

    private void OnFolderMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => Folder.DismissMessage();

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FolderBackupItem item)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Restore Monado's folder?",
            Content = $"Monado's folder from {item.Text} goes back in. The folder as it is now is kept in the list.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Folder.RestoreAsync(item);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderBackupItem item
            && await Pickers.SaveFileAsync(item.ExportName, "Zip archive", ".zip") is { } path)
        {
            await Folder.ExportAsync(item, path);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (await Pickers.OpenFileAsync(".zip") is { } path)
        {
            await Folder.ImportAsync(path);
        }
    }
}
