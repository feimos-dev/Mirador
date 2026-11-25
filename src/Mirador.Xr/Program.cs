using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// Mirador's programs in the headset, one executable: <c>--home</c> is the floor grid shown whenever no game is,
/// <c>--dashboard</c> the bar drawn above every app. Mirador starts both beside Monado and they talk to it over
/// <see cref="XrLink"/>; <c>--alone</c> runs one without Mirador, to look at it. The dashboard starts the third,
/// <c>--desktop</c>, which shows a monitor under it.
/// </summary>
internal static class Program
{
    /// <summary>The folder beside the program that holds its shaders and icons.</summary>
    public const string AssetsFolder = "xr";

    /// <summary>Exit codes: 0 asked to quit, 1 no OpenXR session, 2 a bad command line, 3 no Mirador, 4 no overlay sessions in this runtime.</summary>
    private static int Main(string[] args)
    {
        if (args.Contains("--desktop"))
        {
            // Started by the dashboard, which it talks to on its standard input and output: no StereoKit, no Mirador.
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

    /// <summary>StereoKit's log goes to standard output, which Mirador writes into its own log.</summary>
    private static void OnLog(LogLevel level, string text)
    {
        if (level >= LogLevel.Info)
        {
            Console.WriteLine($"{level}: {text.TrimEnd()}");
        }
    }

    /// <summary>What both programs ask of StereoKit: OpenXR or nothing, the stage as the origin, no window on the desktop.</summary>
    public static SKSettings Settings(string appName) => new()
    {
        appName = appName,
        assetsFolder = AssetsFolder,
        mode = AppMode.XR,
        noFlatscreenFallback = true,
        origin = OriginMode.Stage,
        disableDesktopInputWindow = true,
        // The loop keeps turning while the session is idle, so that Mirador's lines are still read.
        standbyMode = StandbyMode.Slow,
        logFilter = LogLevel.Info,
    };

    /// <summary>Takes Mirador's lines that both programs know; true when the program should go on with <paramref name="line"/> itself.</summary>
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
