using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mirador.Core.Build;

public sealed record BuildTools(string Recipe, string? ToolsDir, string PowerShell)
{
    public static BuildTools For(string appDir, AppPaths paths) => new(
        Path.Combine(appDir, "tools", "build-monado.ps1"),
        paths.ToolsDir,
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));

    public string? ExistingToolsDir => ToolsDir is { } dir && Directory.Exists(dir) ? dir : null;

    public string Git => ToolsDir is { } dir && File.Exists(Path.Combine(dir, "git", "cmd", "git.exe"))
        ? Path.Combine(dir, "git", "cmd", "git.exe")
        : "git";

    public string Ctest => ToolsDir is { } dir && File.Exists(Path.Combine(dir, "cmake", "bin", "ctest.exe"))
        ? Path.Combine(dir, "cmake", "bin", "ctest.exe")
        : "ctest";
}

public enum ToolState
{
    Found,

    Fixable,

    Missing,
}

public sealed record ToolStatus(string Name, ToolState State, string Detail);

public sealed record VsInstance(string Path, string Name, Version Version, bool IsComplete, IReadOnlySet<string> Packages);

public static class VsWhere
{
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");

    public static IReadOnlyList<string> Arguments { get; } = ["-all", "-products", "*", "-include", "packages", "-format", "json", "-utf8"];

    public static IReadOnlyList<VsInstance> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var all = new List<VsInstance>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || Text(item, "installationPath") is not { } path
                    || !Version.TryParse(Text(item, "installationVersion"), out var version))
                {
                    continue;
                }

                var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("packages", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var package in list.EnumerateArray())
                    {
                        if (package.ValueKind == JsonValueKind.Object && Text(package, "id") is { } id)
                        {
                            packages.Add(id);
                        }
                    }
                }

                var complete = !item.TryGetProperty("isComplete", out var flag) || flag.ValueKind != JsonValueKind.False;
                all.Add(new VsInstance(path, Text(item, "displayName") ?? path, version, complete, packages));
            }

            return all;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task<IReadOnlyList<VsInstance>> FindAsync(ICommandRunner runner, string vswhere, CancellationToken ct)
    {
        if (!File.Exists(vswhere))
        {
            return [];
        }

        var lines = new List<string>();
        int code;
        try
        {
            code = await runner.RunAsync(new CommandSpec(vswhere, Arguments), lines.Add, ct).ConfigureAwait(false);
        }
        catch (MiradorException)
        {
            return [];
        }

        return code == 0 ? Parse(string.Join('\n', lines)) : [];
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public static class VsComponents
{
    public const string CppTools = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
    public const string Windows11Sdk = "Microsoft.VisualStudio.Component.Windows11SDK.26100";

    public static IReadOnlyList<string> Required { get; } = [CppTools, Windows11Sdk];

    public static IReadOnlyList<string> Install { get; } =
    [
        "Microsoft.VisualStudio.Component.VC.CoreBuildTools",
        "Microsoft.VisualStudio.ComponentGroup.NativeDesktop.Core",
        "Microsoft.VisualStudio.Component.Windows10SDK",
        Windows11Sdk,
        "Component.Microsoft.Windows.CppWinRT",
        CppTools,
    ];

    public static string Describe(string id) => id switch
    {
        CppTools => "the C++ x64 tools (MSVC)",
        Windows11Sdk => "the Windows 11 SDK (10.0.26100)",
        _ => id,
    };
}

public sealed record VsPlan(ToolState State, VsInstance? Install, IReadOnlyList<string> Missing, string Detail)
{
    public const string Name = "Visual Studio C++ tools";

    public static VsPlan For(IReadOnlyList<VsInstance> all)
    {
        var usable = all.Where(i => i.IsComplete && i.Version.Major >= 17).ToList();
        var target = usable.Where(i => i.Packages.Contains(VsComponents.CppTools)).MaxBy(i => i.Version) ?? usable.MaxBy(i => i.Version);
        if (target is null)
        {
            return new VsPlan(
                ToolState.Missing, null, VsComponents.Install,
                "Not installed: the first branch build installs Microsoft's Build Tools for Visual Studio 2022 with the C++ tools and the Windows 11 SDK (one administrator prompt).");
        }

        var missing = VsComponents.Required.Where(c => !target.Packages.Contains(c)).ToList();
        var name = $"{target.Name} ({target.Version.ToString(2)})";
        return missing.Count == 0
            ? new VsPlan(ToolState.Found, target, [], $"{name}, with the C++ x64 tools and the Windows 11 SDK (10.0.26100).")
            : new VsPlan(
                ToolState.Fixable, target, missing,
                $"{name}, without {string.Join(" and ", missing.Select(VsComponents.Describe))}: the next branch build adds {(missing.Count == 1 ? "it" : "them")} with Visual Studio's installer (one administrator prompt).");
    }

    public CommandSpec Command(string installerDir, string bootstrapper)
    {
        var adds = Missing.SelectMany(c => new[] { "--add", c });
        return State switch
        {
            ToolState.Fixable => new CommandSpec(Path.Combine(installerDir, "setup.exe"), ["modify", "--installPath", Install!.Path, .. adds, "--passive", "--norestart"]),
            ToolState.Missing => new CommandSpec(bootstrapper, [.. adds, "--passive", "--norestart", "--wait"]),
            _ => throw new InvalidOperationException("Visual Studio has what Monado's build needs."),
        };
    }
}

public sealed record ToolPin(string Name, string Folder, string Version, string Url, string Sha256, string Exe);

public static class ToolPins
{
    public static readonly ToolPin Git = new(
        "Git", "git", "2.56.0.4",
        "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.4/MinGit-2.56.0.4-64-bit.zip",
        "e3bf78351fe777fba47ddee5950082f8a98bb0f02604fdc7ba3ee2cb68ffa877", @"cmd\git.exe");

    public static readonly ToolPin CMake = new(
        "CMake", "cmake", "4.4.5",
        "https://github.com/Kitware/CMake/releases/download/v4.4.5/cmake-4.4.5-windows-x86_64.zip",
        "326c826486d46a23cf9d5a1c9ee3a567d427bdd02c4a34071937a2a675cff004", @"bin\cmake.exe");

    public static readonly ToolPin Ninja = new(
        "Ninja", "ninja", "1.13.2",
        "https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip",
        "07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65", "ninja.exe");

    public static IReadOnlyList<ToolPin> All { get; } = [Git, CMake, Ninja];
}

public sealed partial class BuildToolsCheck(string toolsDir, string vcpkgCommit)
{
    public const string MarkerFile = "mirador-tool.json";

    public string VcpkgDir => Path.Combine(toolsDir, "vcpkg");

    public ToolStatus Tool(ToolPin pin)
    {
        var folder = Path.Combine(toolsDir, pin.Folder);
        if (!File.Exists(Path.Combine(folder, pin.Exe)))
        {
            return new ToolStatus(pin.Name, ToolState.Missing, $"Not downloaded yet: the first branch build gets {pin.Version} from GitHub.");
        }

        var marker = ReadMarker(Path.Combine(folder, MarkerFile));
        if (marker is { } m && m.Version == pin.Version && string.Equals(m.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolStatus(pin.Name, ToolState.Found, pin.Version);
        }

        return new ToolStatus(
            pin.Name, ToolState.Fixable,
            $"{marker?.Version ?? "An unknown version"}: the next branch build replaces it with {pin.Version}.");
    }

    public ToolStatus Vcpkg()
    {
        if (!Commit().IsMatch(vcpkgCommit))
        {
            return new ToolStatus("vcpkg", ToolState.Missing, "This Mirador has no vcpkg pin (tools\\vcpkg-commit.txt is missing or wrong): reinstall Mirador.");
        }

        var shortPin = vcpkgCommit[..7];
        var head = Path.Combine(VcpkgDir, ".git", "HEAD");
        if (!File.Exists(head))
        {
            return new ToolStatus("vcpkg", ToolState.Missing, $"Not cloned yet: the first branch build clones it at commit {shortPin}.");
        }

        string text;
        try
        {
            text = File.ReadAllText(head).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ToolStatus("vcpkg", ToolState.Fixable, $"Can't be read: the next branch build clones it again at commit {shortPin}.");
        }

        if (string.Equals(text, vcpkgCommit, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(VcpkgDir, "scripts", "buildsystems", "vcpkg.cmake")))
        {
            return new ToolStatus("vcpkg", ToolState.Found, $"Commit {shortPin}");
        }

        var at = Commit().IsMatch(text) ? "commit " + text[..7] : "a branch";
        return new ToolStatus("vcpkg", ToolState.Fixable, $"At {at}: the next branch build moves it to commit {shortPin}.");
    }

    public static string? ReadPin(string file)
    {
        try
        {
            var text = File.ReadAllText(file).Trim();
            return Commit().IsMatch(text) ? text.ToLowerInvariant() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal sealed record Marker(string Version, string Sha256);

    internal static Marker? ReadMarker(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    internal static partial Regex Commit();
}
