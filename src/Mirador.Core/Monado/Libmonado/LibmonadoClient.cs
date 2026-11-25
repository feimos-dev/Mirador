using System.Runtime.InteropServices;
using System.Text;
using Mirador.Core.Native;

namespace Mirador.Core.Monado.Libmonado;

public sealed record LibmonadoOpen(IMonadoClient? Client, string? Problem);

public static class Libmonado
{
    public const string NotOffered = "This Monado doesn't offer libmonado.";

    public const string FileName = "monado.dll";

    public static unsafe LibmonadoOpen Open(string binDir)
    {
        var path = Path.GetFullPath(Path.Combine(binDir, FileName));
        if (!File.Exists(path))
        {
            return new(null, NotOffered);
        }

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

internal sealed unsafe class LibmonadoClient : IMonadoClient
{
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

    public static string NoAnswer(int result) => $"Monado didn't answer libmonado (error {result}).";

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

                uint origin = 0;
                var originResult = exports.GetDeviceInfoU32(root, i, LibmonadoExports.PropertyTrackingOriginU32, &origin);
                if (originResult != LibmonadoExports.ErrorInvalidProperty)
                {
                    Check(originResult);
                }

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

    public bool OffersApps => exports.OffersApps;

    public bool OffersInputBlocks => exports.OffersInputBlocks;

    public bool OffersStageOffset => exports.OffersReferenceSpaceOffsets;

    public bool OffersLocalRecentre => exports.OffersLocalRecentre;

    private const uint MaxApps = 64;

    public IReadOnlyList<MonadoApp> Apps()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!exports.OffersApps)
            {
                return [];
            }

            Check(exports.UpdateClientList(root));
            uint count;
            Check(exports.GetNumberClients(root, &count));
            if (count > MaxApps)
            {
                throw new MiradorException($"Monado gave libmonado an app count it can't have ({count}).");
            }

            var apps = new List<MonadoApp>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint id, flags;
                byte* name = null;
                if (exports.GetClientIdAtIndex(root, i, &id) < 0 || exports.GetClientName(root, id, &name) < 0)
                {
                    continue;
                }

                var text = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
                if (exports.GetClientState(root, id, &flags) < 0)
                {
                    continue;
                }

                apps.Add(new MonadoApp(
                    id, text,
                    (flags & LibmonadoExports.ClientPrimaryApp) != 0,
                    (flags & LibmonadoExports.ClientSessionActive) != 0,
                    (flags & LibmonadoExports.ClientSessionOverlay) != 0,
                    (flags & LibmonadoExports.ClientInputsBlocked) != 0));
            }

            return apps;
        }
    }

    public void ShowApp(uint id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!exports.OffersApps)
            {
                throw new NotSupportedException("This libmonado doesn't pick the app shown.");
            }

            Check(exports.SetClientPrimary(root, id));
        }
    }

    public void BlockInputs(uint id, bool blocked)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!exports.OffersInputBlocks)
            {
                throw new NotSupportedException("This libmonado doesn't block an app's inputs.");
            }

            Check(exports.SetClientIoBlocks(root, id, blocked ? LibmonadoExports.IoBlockInputs : 0));
        }
    }

    public MonadoPose GetStageOffset()
    {
        lock (gate)
        {
            ThrowUnlessStageOffset();

            MndPose pose;
            Check(exports.GetReferenceSpaceOffset(root, LibmonadoExports.ReferenceSpaceStage, &pose));
            return pose.ToPose();
        }
    }

    public void SetStageOffset(MonadoPose offset)
    {
        lock (gate)
        {
            ThrowUnlessStageOffset();

            var pose = new MndPose(offset);
            Check(exports.SetReferenceSpaceOffset(root, LibmonadoExports.ReferenceSpaceStage, &pose));
        }
    }

    public void RecentreLocalSpaces()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!exports.OffersLocalRecentre)
            {
                throw new NotSupportedException("This libmonado doesn't recentre the local spaces.");
            }

            Check(exports.RecenterLocalSpaces(root));
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

    private void ThrowUnlessStageOffset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!exports.OffersReferenceSpaceOffsets)
        {
            throw new NotSupportedException("This libmonado doesn't move the stage.");
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
