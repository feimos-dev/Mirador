using Microsoft.Win32;
using Mirador.Core.Monado;

namespace Mirador.Core.Runtime;

public interface IRuntimeRegistryReader
{
    string? ActiveRuntime();
}

public sealed class RegistryRuntimeReader : IRuntimeRegistryReader
{
    private const string KeyPath = @"SOFTWARE\Khronos\OpenXR\1";
    private const string ValueName = "ActiveRuntime";

    private readonly Func<RegistryView, string, string, object?> readValue;

    public RegistryRuntimeReader() : this(ReadLocalMachine)
    {
    }

    internal RegistryRuntimeReader(Func<RegistryView, string, string, object?> readValue) => this.readValue = readValue;

    public string? ActiveRuntime() => readValue(RegistryView.Registry64, KeyPath, ValueName) as string;

    private static object? ReadLocalMachine(RegistryView view, string keyPath, string valueName)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = machine.OpenSubKey(keyPath);
        return key?.GetValue(valueName);
    }
}

public sealed class OpenXrRuntime(IRuntimeRegistryReader reader)
{
    public string? Active()
    {
        var stored = reader.ActiveRuntime();
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    public bool IsActive(string manifest)
    {
        var active = FullPathOf(reader.ActiveRuntime());
        var wanted = FullPathOf(manifest);
        return active is not null && wanted is not null && string.Equals(active, wanted, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsActive(MonadoInstall install) => install.RuntimeManifests.Any(IsActive);

    private static string? FullPathOf(string? value)
    {
        var path = value?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
