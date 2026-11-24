using System.Runtime.InteropServices;

namespace Mirador.Core.Monado.Libmonado;

/// <summary>Loading <c>monado.dll</c> and finding its functions.</summary>
internal static class LibmonadoNative
{
    /// <summary><c>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR</c>: the loaded DLL's own folder is searched for its dependencies.</summary>
    public const uint SearchDllLoadDir = 0x100;

    /// <summary><c>LOAD_LIBRARY_SEARCH_SYSTEM32</c>: System32 is searched for its dependencies.</summary>
    public const uint SearchSystem32 = 0x800;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr module);
}

/// <summary><c>mnd_pose_t</c>: orientation x, y, z, w, then position x, y, z.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MndPose
{
    public float Qx, Qy, Qz, Qw, X, Y, Z;

    public MndPose(MonadoPose pose) => (Qx, Qy, Qz, Qw, X, Y, Z) = pose;

    public readonly MonadoPose ToPose() => new(Qx, Qy, Qz, Qw, X, Y, Z);
}

/// <summary>
/// The libmonado functions Mirador calls (<c>monado.h</c>, API version 1). Results are <c>mnd_result_t</c>: negative
/// on error, zero or positive on success. C <c>bool</c>s are single bytes.
/// </summary>
internal sealed unsafe class LibmonadoExports
{
    /// <summary><c>MND_ERROR_INVALID_PROPERTY</c>: the library doesn't know the property asked for.</summary>
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

    /// <summary>The tracking-origin functions; null when the library lacks any of them.</summary>
    public delegate* unmanaged[Cdecl]<IntPtr, uint*, int> GetTrackingOriginCount;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, byte**, int> GetTrackingOriginName;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, MndPose*, int> GetTrackingOriginOffset;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, MndPose*, int> SetTrackingOriginOffset;

    /// <summary>The app functions; null when the library lacks any of them.</summary>
    public delegate* unmanaged[Cdecl]<IntPtr, int> UpdateClientList;
    public delegate* unmanaged[Cdecl]<IntPtr, uint*, int> GetNumberClients;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int> GetClientIdAtIndex;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, byte**, int> GetClientName;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int> GetClientState;
    public delegate* unmanaged[Cdecl]<IntPtr, uint, int> SetClientPrimary;

    /// <summary><c>mnd_root_set_client_io_blocks</c> (API 1.6); null when the library lacks it.</summary>
    public delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int> SetClientIoBlocks;

    /// <summary>The reference-space offset functions; null when the library lacks either.</summary>
    public delegate* unmanaged[Cdecl]<IntPtr, int, MndPose*, int> GetReferenceSpaceOffset;
    public delegate* unmanaged[Cdecl]<IntPtr, int, MndPose*, int> SetReferenceSpaceOffset;

    /// <summary><c>mnd_root_recenter_local_spaces</c>; null when the library lacks it.</summary>
    public delegate* unmanaged[Cdecl]<IntPtr, int> RecenterLocalSpaces;

    /// <summary><c>mnd_client_flags_t</c>.</summary>
    public const uint ClientPrimaryApp = 1u << 0;
    public const uint ClientSessionActive = 1u << 1;
    public const uint ClientSessionOverlay = 1u << 4;
    public const uint ClientInputsBlocked = 1u << 8;

    /// <summary><c>MND_IO_BLOCK_INPUTS</c>.</summary>
    public const uint IoBlockInputs = 1u << 2;

    /// <summary><c>MND_SPACE_REFERENCE_TYPE_STAGE</c>.</summary>
    public const int ReferenceSpaceStage = 3;

    private LibmonadoExports() { }

    /// <summary>For tests: no functions yet; the test sets the ones it needs.</summary>
    internal static LibmonadoExports ForTests() => new();

    /// <summary>Whether the tracking-origin functions were all found.</summary>
    public bool OffersOriginOffsets => GetTrackingOriginCount != null;

    /// <summary>Whether the app functions were all found.</summary>
    public bool OffersApps => UpdateClientList != null;

    public bool OffersInputBlocks => SetClientIoBlocks != null;

    /// <summary>Whether the reference-space offset functions were both found.</summary>
    public bool OffersReferenceSpaceOffsets => GetReferenceSpaceOffset != null;

    public bool OffersLocalRecentre => RecenterLocalSpaces != null;

    /// <summary>The functions of <paramref name="module"/>; null when it lacks one every client needs.</summary>
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
