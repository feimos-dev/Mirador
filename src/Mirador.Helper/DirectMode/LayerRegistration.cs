using System.Security;
using Microsoft.Win32;
using Mirador.Ipc;

namespace Mirador.Helper.DirectMode;

public interface IImplicitLayers
{
    void Add(string manifest);

    void Remove(string manifest);
}

public sealed class SystemImplicitLayers : IImplicitLayers
{
    private const string Key = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";

    public void Add(string manifest)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(Key, writable: true);
        key.SetValue(manifest, 0, RegistryValueKind.DWord);
    }

    public void Remove(string manifest)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Key, writable: true);
        key?.DeleteValue(manifest, throwOnMissingValue: false);
    }
}

public sealed class LayerRegistration(IImplicitLayers layers, string installDir)
{
    private const string UnknownLayer = "Mirador's helper service doesn't know that direct-mode layer.";

    public string NvidiaManifest { get; } = Path.Combine(installDir, "layer", DirectModeEnvironment.ManifestName);

    public HelperReply Register(string? kind)
    {
        if (kind != DirectModeEnvironment.Nvidia)
        {
            return new HelperReply(false, UnknownLayer);
        }

        if (!File.Exists(NvidiaManifest))
        {
            return new HelperReply(false, $"The direct-mode layer that comes with Mirador is missing ({NvidiaManifest}). Reinstall Mirador.");
        }

        return Change(() => layers.Add(NvidiaManifest));
    }

    public HelperReply Unregister(string? kind) =>
        kind == DirectModeEnvironment.Nvidia ? Change(() => layers.Remove(NvidiaManifest)) : new HelperReply(false, UnknownLayer);

    private static HelperReply Change(Action change)
    {
        try
        {
            change();
            return new HelperReply(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new HelperReply(false, $"Windows wouldn't change the Vulkan layers ({e.Message.Trim().TrimEnd('.')}).");
        }
    }
}
