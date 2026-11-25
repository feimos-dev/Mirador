using Windows.Storage.Pickers;

namespace Mirador.App.Services;

public static class Pickers
{
    public static async Task<string?> FolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        Bind(picker);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> SaveFileAsync(string suggestedName, string typeName, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeChoices.Add(typeName, [extension]);
        Bind(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> OpenFileAsync(string extension)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add(extension);
        Bind(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static void Bind(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow));
}
