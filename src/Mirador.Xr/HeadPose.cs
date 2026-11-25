using System.Runtime.InteropServices;
using StereoKit;

namespace Mirador.Xr;

internal static unsafe class HeadPose
{
    private const int SpaceLocationType = 42;
    private const ulong OrientationValid = 0x1;
    private const ulong PositionValid = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SpaceLocation
    {
        public int Type;
        public IntPtr Next;
        public ulong Flags;
        public fixed float Pose[7];
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
