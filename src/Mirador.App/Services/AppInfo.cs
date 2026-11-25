using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Monado;
using Mirador.Core.OpenVr;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.App.Services;

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string Windows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var build = key?.GetValue("CurrentBuild") as string ?? "";

            if (int.TryParse(build, out var number) && number >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            }

            var display = key?.GetValue("DisplayVersion") as string;
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            return $"{product}{(display is null ? "" : " " + display)} (build {build}{ubr})";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    public static string Monado(Profile profile, string appDir)
    {
        switch (profile.Monado.Kind)
        {
            case SourceKind.Folder:
                return $"build folder {profile.Monado.Folder}";
            case SourceKind.Mirador:
                return $"the bundled Monado {MiradorMonado.Read(Path.Combine(appDir, "monado")).Current ?? "(not installed)"}";
            case SourceKind.Branch:
                return $"{profile.Monado.Describe()}{BuiltCommit(profile)}";
        }

        return profile.Monado.Describe();
    }

    private static string BuiltCommit(Profile profile)
    {
        var info = BuildInfo.Load(Path.Combine(AppPaths.Default.BuildsDir, profile.Id, "install"));
        return info is null ? " (not built yet)" : $" at {info.ShortCommit}, {info.Compiler}";
    }

    public static string Versions(Profile profile, string appDir) =>
        $"Mirador {Version}\n{Windows()}\nMonado: {Monado(profile, appDir)}\n" +
        $"{new OpenCompositeInstall(OpenCompositeInstall.DefaultFolder).VersionLine()}\n" +
        $"Profile: {profile.Name}\n";
}
