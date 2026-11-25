using System.Runtime.InteropServices;
using StereoKit;

namespace Mirador.Xr;

internal static unsafe class ControllerModels
{
    private const int InteractionProfileChanged = 52;
    private const int InteractionProfileState = 53;

    private static readonly Dictionary<string, string> Folders = new()
    {
        ["/interaction_profiles/valve/index_controller"] = "valve-index",
        ["/interaction_profiles/htc/vive_controller"] = "htc-vive",
        ["/interaction_profiles/oculus/touch_controller"] = "oculus-touch-v3",
        ["/interaction_profiles/microsoft/motion_controller"] = "microsoft-mixed-reality",
        ["/interaction_profiles/hp/mixed_reality_controller"] = "hp-mixed-reality",
    };

    private static readonly Dictionary<string, Model?> Loaded = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct ProfileState
    {
        public int Type;
        public IntPtr Next;
        public ulong Profile;
    }

    public static void Follow()
    {
        Backend.OpenXR.OnPollEvent += data =>
        {
            if (Marshal.ReadInt32(data) == InteractionProfileChanged)
            {
                Update();
            }
        };
        Update();
    }

    private static void Update()
    {
        var stringToPath = (delegate* unmanaged<ulong, byte*, ulong*, int>)Backend.OpenXR.GetFunctionPtr("xrStringToPath");
        var pathToString = (delegate* unmanaged<ulong, ulong, uint, uint*, byte*, int>)Backend.OpenXR.GetFunctionPtr("xrPathToString");
        var currentProfile = (delegate* unmanaged<ulong, ulong, ProfileState*, int>)Backend.OpenXR.GetFunctionPtr("xrGetCurrentInteractionProfile");
        if (stringToPath == null || pathToString == null || currentProfile == null)
        {
            return;
        }

        foreach (var (handed, user, side) in new[] { (Handed.Left, "/user/hand/left", "left"), (Handed.Right, "/user/hand/right", "right") })
        {
            ulong path;
            var state = new ProfileState { Type = InteractionProfileState };
            var name = Marshal.StringToCoTaskMemUTF8(user);
            try
            {
                if (stringToPath(Backend.OpenXR.Instance, (byte*)name, &path) < 0 || currentProfile(Backend.OpenXR.Session, path, &state) < 0)
                {
                    continue;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }

            Input.ControllerModelSet(handed, ModelFor(ProfileName(pathToString, state.Profile), side));
        }
    }

    private static string? ProfileName(delegate* unmanaged<ulong, ulong, uint, uint*, byte*, int> pathToString, ulong profile)
    {
        if (profile == 0)
        {
            return null;
        }

        var text = stackalloc byte[256];
        uint length;
        return pathToString(Backend.OpenXR.Instance, profile, 256, &length, text) < 0 ? null : Marshal.PtrToStringUTF8((IntPtr)text);
    }

    private static Model? ModelFor(string? profile, string side)
    {
        if (profile is null || !Folders.TryGetValue(profile, out var folder))
        {
            return null;
        }

        var key = $"{folder}/{side}";
        if (!Loaded.TryGetValue(key, out var model))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, Program.AssetsFolder, "controllers", folder);
            var file = new[] { $"{side}.glb", "none.glb" }.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists);
            model = file is null ? null : Model.FromFile(file);
            for (var node = model?.RootNode; node is not null; node = node.Sibling)
            {
                node.LocalTransform *= Matrix.R(0, 180, 0);
            }

            Loaded[key] = model;
        }

        return model;
    }
}
