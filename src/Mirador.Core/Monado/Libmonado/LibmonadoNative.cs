using System.Runtime.InteropServices;

namespace Mirador.Core.Monado.Libmonado;

internal static class LibmonadoNative
{
    public const uint SearchDllLoadDir = 0x100;

    public const uint SearchSystem32 = 0x800;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr module);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MndPose
{
    public float Qx, Qy, Qz, Qw, X, Y, Z;

    public MndPose(MonadoPose pose) => (Qx, Qy, Qz, Qw, X, Y, Z) = pose;

    public readonly MonadoPose ToPose() => new(Qx, Qy, Qz, Qw, X, Y, Z);
}

internal sealed unsafe class LibmonadoExports
{
    public const int ErrorInvalidProperty = -6;

    public const int PropertyNameString = 0;
    public const int PropertySerialString = 1;
    public const int PropertyTrackingOriginU32 = 2;

    public delegate* unmanaged[Cdecl]<uint*, uint*, uint*, void> ApiGetVersion;
    public delegate* unmanaged[Cdecl]<IntPtr*, int> RootCreate;
    public delegate* unmanaged[Cdecl]<IntPtr*, void> RootDestroy;
    public delegate* unmanaged[Cdecl]<IntPtr, uint*, int> GetDeviceCount;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, int, byte**, int> GetDeviceInfoString;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, int, uint*, int> GetDeviceInfoU32;
    public delegate* unmanaged[Cdecl]<IntPtr, byte*, int*, int> GetDeviceFromRole;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, byte*, byte*, float*, int> GetDeviceBatteryStatus;

    public delegate* unmanaged[Cdecl]<IntPtr, uint*, int> GetTrackingOriginCount;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, byte**, int> GetTrackingOriginName;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, MndPose*, int> GetTrackingOriginOffset;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, MndPose*, int> SetTrackingOriginOffset;

    public delegate* unmanaged[Cdecl]<IntPtr, int> UpdateClientList;
    public delegate* unmanaged[Cdecl]<IntPtr, uint*, int> GetNumberClients;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int> GetClientIdAtIndex;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, byte**, int> GetClientName;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int> GetClientState;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, int> SetClientPrimary;

    public delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int> SetClientIoBlocks;

    public delegate* unmanaged[Cdecl]<IntPtr, int, MndPose*, int> GetReferenceSpaceOffset;
    public delegate* unmanaged[Cdecl]<IntPtr, int, MndPose*, int> SetReferenceSpaceOffset;

    public delegate* unmanaged[Cdecl]<IntPtr, int> RecenterLocalSpaces;

    public const uint ClientPrimaryApp = 1u << 0;
    public const uint ClientSessionActive = 1u << 1;
    public const uint ClientSessionOverlay = 1u << 4;
    public const uint ClientInputsBlocked = 1u << 8;

    public const uint IoBlockInputs = 1u << 2;

    public const int ReferenceSpaceStage = 3;

    private LibmonadoExports() { }

    internal static LibmonadoExports ForTests() => new();

    public bool OffersOriginOffsets => GetTrackingOriginCount != null;

    public bool OffersApps => UpdateClientList != null;

    public bool OffersInputBlocks => SetClientIoBlocks != null;

    public bool OffersReferenceSpaceOffsets => GetReferenceSpaceOffset != null;

    public bool OffersLocalRecentre => RecenterLocalSpaces != null;

    public static LibmonadoExports? Resolve(IntPtr module)
    {
        var apiGetVersion = LibmonadoNative.GetProcAddress(module, "mnd_api_get_version");
        var rootCreate = LibmonadoNative.GetProcAddress(module, "mnd_root_create");
        var rootDestroy = LibmonadoNative.GetProcAddress(module, "mnd_root_destroy");
        var deviceCount = LibmonadoNative.GetProcAddress(module, "mnd_root_get_device_count");
        var deviceInfoString = LibmonadoNative.GetProcAddress(module, "mnd_root_get_device_info_string");
        var deviceInfoU32 = LibmonadoNative.GetProcAddress(module, "mnd_root_get_device_info_u32");
        var deviceFromRole = LibmonadoNative.GetProcAddress(module, "mnd_root_get_device_from_role");
        var batteryStatus = LibmonadoNative.GetProcAddress(module, "mnd_root_get_device_battery_status");
        if (apiGetVersion == 0 || rootCreate == 0 || rootDestroy == 0 || deviceCount == 0 || deviceInfoString == 0
            || deviceInfoU32 == 0 || deviceFromRole == 0 || batteryStatus == 0)
        {
            return null;
        }

        var exports = new LibmonadoExports
        {
            ApiGetVersion = (delegate* unmanaged[Cdecl]<uint*, uint*, uint*, void>)apiGetVersion,
            RootCreate = (delegate* unmanaged[Cdecl]<IntPtr*, int>)rootCreate,
            RootDestroy = (delegate* unmanaged[Cdecl]<IntPtr*, void>)rootDestroy,
            GetDeviceCount = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)deviceCount,
            GetDeviceInfoString = (delegate* unmanaged[Cdecl]<IntPtr, uint, int, byte**, int>)deviceInfoString,
            GetDeviceInfoU32 = (delegate* unmanaged[Cdecl]<IntPtr, uint, int, uint*, int>)deviceInfoU32,
            GetDeviceFromRole = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int*, int>)deviceFromRole,
            GetDeviceBatteryStatus = (delegate* unmanaged[Cdecl]<IntPtr, uint, byte*, byte*, float*, int>)batteryStatus,
        };

        var originCount = LibmonadoNative.GetProcAddress(module, "mnd_root_get_tracking_origin_count");
        var originName = LibmonadoNative.GetProcAddress(module, "mnd_root_get_tracking_origin_name");
        var getOriginOffset = LibmonadoNative.GetProcAddress(module, "mnd_root_get_tracking_origin_offset");
        var setOriginOffset = LibmonadoNative.GetProcAddress(module, "mnd_root_set_tracking_origin_offset");
        if (originCount != 0 && originName != 0 && getOriginOffset != 0 && setOriginOffset != 0)
        {
            exports.GetTrackingOriginCount = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)originCount;
            exports.GetTrackingOriginName = (delegate* unmanaged[Cdecl]<IntPtr, uint, byte**, int>)originName;
            exports.GetTrackingOriginOffset = (delegate* unmanaged[Cdecl]<IntPtr, uint, MndPose*, int>)getOriginOffset;
            exports.SetTrackingOriginOffset = (delegate* unmanaged[Cdecl]<IntPtr, uint, MndPose*, int>)setOriginOffset;
        }

        var updateClients = LibmonadoNative.GetProcAddress(module, "mnd_root_update_client_list");
        var numberClients = LibmonadoNative.GetProcAddress(module, "mnd_root_get_number_clients");
        var clientIdAtIndex = LibmonadoNative.GetProcAddress(module, "mnd_root_get_client_id_at_index");
        var clientName = LibmonadoNative.GetProcAddress(module, "mnd_root_get_client_name");
        var clientState = LibmonadoNative.GetProcAddress(module, "mnd_root_get_client_state");
        var setClientPrimary = LibmonadoNative.GetProcAddress(module, "mnd_root_set_client_primary");
        if (updateClients != 0 && numberClients != 0 && clientIdAtIndex != 0 && clientName != 0 && clientState != 0 && setClientPrimary != 0)
        {
            exports.UpdateClientList = (delegate* unmanaged[Cdecl]<IntPtr, int>)updateClients;
            exports.GetNumberClients = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)numberClients;
            exports.GetClientIdAtIndex = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int>)clientIdAtIndex;
            exports.GetClientName = (delegate* unmanaged[Cdecl]<IntPtr, uint, byte**, int>)clientName;
            exports.GetClientState = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int>)clientState;
            exports.SetClientPrimary = (delegate* unmanaged[Cdecl]<IntPtr, uint, int>)setClientPrimary;
        }

        exports.SetClientIoBlocks =
            (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int>)LibmonadoNative.GetProcAddress(module, "mnd_root_set_client_io_blocks");

        var getSpaceOffset = LibmonadoNative.GetProcAddress(module, "mnd_root_get_reference_space_offset");
        var setSpaceOffset = LibmonadoNative.GetProcAddress(module, "mnd_root_set_reference_space_offset");
        if (getSpaceOffset != 0 && setSpaceOffset != 0)
        {
            exports.GetReferenceSpaceOffset = (delegate* unmanaged[Cdecl]<IntPtr, int, MndPose*, int>)getSpaceOffset;
            exports.SetReferenceSpaceOffset = (delegate* unmanaged[Cdecl]<IntPtr, int, MndPose*, int>)setSpaceOffset;
        }

        exports.RecenterLocalSpaces =
            (delegate* unmanaged[Cdecl]<IntPtr, int>)LibmonadoNative.GetProcAddress(module, "mnd_root_recenter_local_spaces");

        return exports;
    }
}
