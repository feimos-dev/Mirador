using System.Diagnostics;

namespace Mirador.Core.Monado;

public static class StandIn
{
    public const string FileDescription = "Mirador stand-in";

    public const string FileName = Mirador.Ipc.StandInNames.FileName;

    public const string ExecutableName = Mirador.Ipc.StandInNames.ExecutableName;

    public static IReadOnlyList<string> FileNames => Mirador.Ipc.StandInNames.FileNames;

    public static bool IsStandIn(string imagePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(imagePath).FileDescription == FileDescription;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string? ProgramOf(int? processId, Func<int, string?> imagePathOf) =>
        processId is { } id && imagePathOf(id) is { } path && IsStandIn(path) ? path : null;

    public static bool BelongsTo(string standIn, MonadoInstall install) =>
        install.StandInDirs.Any(dir => FileNames.Any(name => IsFile(standIn, Path.Combine(dir, name))));

    private static bool IsFile(string standIn, string expected) =>
        Native.Win32.FileIdentity(standIn) is { } a && Native.Win32.FileIdentity(expected) is { } b
            ? a == b
            : string.Equals(Path.GetFullPath(standIn), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
}
