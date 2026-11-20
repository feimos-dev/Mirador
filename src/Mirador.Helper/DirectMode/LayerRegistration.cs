using System.Security;
using Microsoft.Win32;
using Mirador.Ipc;

namespace Mirador.Helper.DirectMode;

/// <summary>Vulkan's implicit layers in the 64-bit view of <c>HKEY_LOCAL_MACHINE</c>, which every program's loader reads.</summary>
public interface IImplicitLayers
{
    void Add(string manifest);

    /// <summary>Takes <paramref name="manifest"/> out; one that isn't there is fine.</summary>
    void Remove(string manifest);
}

/// <summary>The real <see cref="IImplicitLayers"/>: values under <c>SOFTWARE\Khronos\Vulkan\ImplicitLayers</c>, each a manifest path set to 0.</summary>
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

/// <summary>
/// Registers the direct-mode layer that comes with Mirador (<c>{install}\layer</c>) while a profile uses it, and takes it
/// out again. A request names the layer's kind, never a path, so nothing else can be registered this way.
/// </summary>
public sealed class LayerRegistration(IImplicitLayers layers, string installDir)
{
    private const string UnknownLayer = "Mirador's helper service doesn't know that direct-mode layer.";

    /// <summary>The manifest the NVIDIA layer is registered by.</summary>
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
