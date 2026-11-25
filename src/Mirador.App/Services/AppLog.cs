using System.Globalization;
using Mirador.Core;

namespace Mirador.App.Services;

public static class AppLog
{
    private static TextLog? log;

    public static IReadOnlyList<string> Tail => log?.Tail ?? [];

    public static event Action<string>? LineWritten;

    public static void Open(AppPaths paths) => log ??= new TextLog(paths.AppLogFile);

    public static void Write(string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}";
        try
        {
            log?.Write(line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        LineWritten?.Invoke(line);
    }

    public static void Write(string what, Exception e) => Write($"{what}: {e}");

    public static void Close()
    {
        log?.Dispose();
        log = null;
    }
}
