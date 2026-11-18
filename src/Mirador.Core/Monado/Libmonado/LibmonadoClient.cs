using System.Runtime.InteropServices;
using System.Text;
using Mirador.Core.Native;

namespace Mirador.Core.Monado.Libmonado;

/// <summary>What <see cref="Libmonado.Open"/> gave: a connected client, or the problem that stopped it (one of them is null).</summary>
public sealed record LibmonadoOpen(IMonadoClient? Client, string? Problem);

/// <summary>
/// Libmonado, Monado's client library, loaded from a Monado install's <c>bin</c> folder. Mirador never ships its own
/// copy: the library must speak the same protocol as the service it connects to.
/// </summary>
public static class Libmonado
{
    /// <summary>The problem when the install has no libmonado, or one without the functions Mirador needs.</summary>
    public const string NotOffered = "This Monado doesn't offer libmonado.";

    /// <summary>Libmonado's file name in a Monado install's <c>bin</c> folder.</summary>
    public const string FileName = "monado.dll";

    /// <summary>
    /// Loads <see cref="FileName"/> from <paramref name="binDir"/> and connects to Monado through it. Call it only when
    /// Monado is known to run: when Monado's pipe is missing, libmonado starts Monado itself.
    /// </summary>
    public static unsafe LibmonadoOpen Open(string binDir)
    {
        var path = Path.GetFullPath(Path.Combine(binDir, FileName));
        if (!File.Exists(path))
        {
            return new(null, NotOffered);
        }

        // Its dependencies come from bin and System32 only, never from the current folder.
        var module = Win32.WithoutErrorBoxes(() =>
            LibmonadoNative.LoadLibraryExW(path, IntPtr.Zero, LibmonadoNative.SearchDllLoadDir | LibmonadoNative.SearchSystem32));
        if (module == IntPtr.Zero)
        {
            return new(null, NotOffered);
        }

        try
        {
            if (LibmonadoExports.Resolve(module) is not { } exports)
            {
                LibmonadoNative.FreeLibrary(module);
                return new(null, NotOffered);
            }

            uint major, minor, patch;
            exports.ApiGetVersion(&major, &minor, &patch);
            if (major != 1)
            {
                LibmonadoNative.FreeLibrary(module);
                return new(null, NotOffered);
            }

            IntPtr root;
            var result = exports.RootCreate(&root);
            if (result < 0)
            {
                LibmonadoNative.FreeLibrary(module);
                return new(null, LibmonadoClient.NoAnswer(result));
            }

            return new(new LibmonadoClient(module, exports, root), null);
        }
        catch
        {
            LibmonadoNative.FreeLibrary(module);
            throw;
        }
    }
}

/// <summary>An open libmonado connection: <c>mnd_root_t</c> and the library it came from. Calls are serialised.</summary>
internal sealed unsafe class LibmonadoClient : IMonadoClient
{
    /// <summary>The roles asked for, in this order; a device gets the first one it has.</summary>
    private static readonly (string Name, byte[] Native)[] Roles =
        [.. new[] { "head", "eyes", "left", "right", "gamepad", "hand-tracking-unobstructed-left", "hand-tracking-unobstructed-right" }
            .Select(role => (role, Encoding.ASCII.GetBytes(role + "\0")))];

    private readonly object gate = new();
    private readonly IntPtr module;
    private readonly LibmonadoExports exports;
    private IntPtr root;
    private bool disposed;

    public LibmonadoClient(IntPtr module, LibmonadoExports exports, IntPtr root)
    {
        this.module = module;
        this.exports = exports;
        this.root = root;
    }

    public bool OffersOriginOffsets => exports.OffersOriginOffsets;

    /// <summary>The problem when a libmonado call returns <paramref name="result"/>.</summary>
    public static string NoAnswer(int result) => $"Monado didn't answer libmonado (error {result}).";

    /// <summary>More devices than Monado can hold (its system has 32 at most): a count over this is a broken answer.</summary>
    private const uint MaxDevices = 64;

    public IReadOnlyList<MonadoDevice> Devices()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            uint count;
            Check(exports.GetDeviceCount(root, &count));
            if (count > MaxDevices)
            {
                throw new MiradorException($"Monado gave libmonado a device count it can't have ({count}).");
            }

            var roles = new Dictionary<int, string>();
            foreach (var (name, native) in Roles)
            {
                int index;
                fixed (byte* role = native)
                {
                    // A role this libmonado doesn't know, or that no device has, is skipped.
                    if (exports.GetDeviceFromRole(root, role, &index) >= 0 && index >= 0)
                    {
                        roles.TryAdd(index, name);
                    }
                }
            }

            var devices = new List<MonadoDevice>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var name = DeviceString(i, LibmonadoExports.PropertyNameString) ?? "";
                var serial = DeviceString(i, LibmonadoExports.PropertySerialString) is { Length: > 0 } text ? text : null;

                // A libmonado older than 1.4 doesn't know this property; its devices count as on origin 0.
                uint origin = 0;
                var originResult = exports.GetDeviceInfoU32(root, i, LibmonadoExports.PropertyTrackingOriginU32, &origin);
                if (originResult != LibmonadoExports.ErrorInvalidProperty)
                {
                    Check(originResult);
                }

                // Fails for a device without battery status.
                byte present = 0, charging = 0;
                float charge = 0;
                var hasBattery = exports.GetDeviceBatteryStatus(root, i, &present, &charging, &charge) >= 0 && present != 0;

                devices.Add(new MonadoDevice(
                    (int)i, roles.GetValueOrDefault((int)i), name, serial, origin,
                    hasBattery, hasBattery && charging != 0, hasBattery ? charge : 0));
            }

            return devices;
        }
    }

    public IReadOnlyList<string> TrackingOrigins()
    {
        lock (gate)
        {
            ThrowUnlessOriginOffsets();

            uint count;
            Check(exports.GetTrackingOriginCount(root, &count));

            var names = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                byte* name = null;
                Check(exports.GetTrackingOriginName(root, i, &name));
                names.Add(Marshal.PtrToStringUTF8((IntPtr)name) ?? "");
            }

            return names;
        }
    }

    public MonadoPose GetOriginOffset(uint origin)
    {
        lock (gate)
        {
            ThrowUnlessOriginOffsets();

            MndPose pose;
            Check(exports.GetTrackingOriginOffset(root, origin, &pose));
            return pose.ToPose();
        }
    }

    public void SetOriginOffset(uint origin, MonadoPose offset)
    {
        lock (gate)
        {
            ThrowUnlessOriginOffsets();

            var pose = new MndPose(offset);
            Check(exports.SetTrackingOriginOffset(root, origin, &pose));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            var handle = root;
            exports.RootDestroy(&handle);
            root = IntPtr.Zero;
            LibmonadoNative.FreeLibrary(module);
        }
    }

    private string? DeviceString(uint index, int property)
    {
        byte* text = null;
        Check(exports.GetDeviceInfoString(root, index, property, &text));
        return Marshal.PtrToStringUTF8((IntPtr)text);
    }

    private void ThrowUnlessOriginOffsets()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!exports.OffersOriginOffsets)
        {
            throw new NotSupportedException("This libmonado doesn't move tracking origins.");
        }
    }

    private static void Check(int result)
    {
        if (result < 0)
        {
            throw new MiradorException(NoAnswer(result));
        }
    }
}
