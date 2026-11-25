using System.Runtime.InteropServices;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// Whether OpenXR knows where the head is. Until it does, <see cref="Input.Head"/> is the stage's origin, on the floor,
/// and nothing should be placed from it.
/// </summary>
internal static unsafe class HeadPose
{
    private const int SpaceLocationType = 42; // XR_TYPE_SPACE_LOCATION
    private const ulong OrientationValid = 0x1; // XR_SPACE_LOCATION_ORIENTATION_VALID_BIT
    private const ulong PositionValid = 0x2; // XR_SPACE_LOCATION_POSITION_VALID_BIT

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SpaceLocation
    {
        public int Type;
        public IntPtr Next;
        public ulong Flags;
        public fixed float Pose[7]; // XrPosef: orientation x, y, z, w, then position x, y, z
    }

    public static bool Known()
    {
        var locate = (delegate* unmanaged<ulong, ulong, long, SpaceLocation*, int>)Backend.OpenXR.GetFunctionPtr("xrLocateSpace");
        if (locate == null)
        {
            return false;
        }

        var location = new SpaceLocation { Type = SpaceLocationType };
        return locate(Backend.OpenXR.HeadSpace, Backend.OpenXR.Space, Backend.OpenXR.Time, &location) >= 0
            && (location.Flags & (OrientationValid | PositionValid)) == (OrientationValid | PositionValid);
    }
}
