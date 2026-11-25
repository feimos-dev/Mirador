using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

internal static class Program
{
    public const string AssetsFolder = "xr";

    private static int Main(string[] args)
    {
        if (args.Contains("--desktop"))
        {
            return DesktopLayerApp.Run(Console.In);
        }

        var role = args.Contains("--home") ? XrLink.HomeRole : args.Contains("--dashboard") ? XrLink.DashboardRole : null;
        if (role is null)
        {
            return 2;
        }

        using var link = args.Contains("--alone") ? Link.Alone() : Link.Connect(role);
        if (link is null)
        {
            Console.Error.WriteLine("Mirador isn't running.");
            return 3;
        }

        Log.Subscribe(OnLog);
        return role == XrLink.HomeRole ? HomeApp.Run(link) : DashboardApp.Run(link);
    }

    private static void OnLog(LogLevel level, string text)
    {
        if (level >= LogLevel.Info)
        {
            Console.WriteLine($"{level}: {text.TrimEnd()}");
        }
    }

    public static SKSettings Settings(string appName) => new()
    {
        appName = appName,
        assetsFolder = AssetsFolder,
        mode = AppMode.XR,
        noFlatscreenFallback = true,
        origin = OriginMode.Stage,
        disableDesktopInputWindow = true,
        standbyMode = StandbyMode.Slow,
        logFilter = LogLevel.Info,
    };

    public static bool Next(Link link, out string word, out string value)
    {
        while (link.TryRead(out var line))
        {
            (word, value) = XrLink.Split(line);
            if (word == XrLink.Quit)
            {
                SK.Quit();
                continue;
            }

            return true;
        }

        if (link.Lost)
        {
            SK.Quit();
        }

        (word, value) = ("", "");
        return false;
    }
}
