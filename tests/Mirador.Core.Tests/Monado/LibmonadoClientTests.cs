using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Tests.Monado;

public unsafe class LibmonadoClientTests
{
    [Fact]
    public void Open_without_monado_dll_says_libmonado_is_not_offered()
    {
        using var bin = new TempDir();

        var opened = Libmonado.Open(bin.Path);

        Assert.Null(opened.Client);
        Assert.Equal(Libmonado.NotOffered, opened.Problem);
    }

    [Fact]
    public void Open_with_a_dll_lacking_the_functions_says_libmonado_is_not_offered()
    {
        using var bin = new TempDir();
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), bin.File(Libmonado.FileName));

        var opened = Libmonado.Open(bin.Path);

        Assert.Null(opened.Client);
        Assert.Equal(Libmonado.NotOffered, opened.Problem);
    }

    /// <summary>
    /// Needs Monado started by hand from the install <c>MIRADOR_TEST_MONADO_INSTALL</c> names, built with libmonado.
    /// Without Monado running, libmonado's connection would start it.
    /// </summary>
    [MonadoInstallFact]
    public void Devices_of_a_running_monado_include_the_head()
    {
        var install = Environment.GetEnvironmentVariable(MonadoInstallFactAttribute.Variable)!;

        var opened = Libmonado.Open(Path.Combine(install, "bin"));

        Assert.Null(opened.Problem);
        using var client = opened.Client!;
        Assert.Single(client.Devices(), device => device.Role == "head");
    }

    // A libmonado that says it has more devices than Monado can have.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HugeCount(IntPtr root, uint* count)
    {
        *count = uint.MaxValue;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NoRole(IntPtr root, byte* role, int* index)
    {
        *index = -1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroy(IntPtr* root) => *root = IntPtr.Zero;

    [Fact]
    public void An_impossible_device_count_is_a_failed_call_not_a_crash()
    {
        var exports = LibmonadoExports.ForTests();
        exports.GetDeviceCount = &HugeCount;
        exports.GetDeviceFromRole = &NoRole;
        exports.RootDestroy = &Destroy;
        using var client = new LibmonadoClient(IntPtr.Zero, exports, new IntPtr(1));

        var e = Assert.Throws<MiradorException>(() => client.Devices());

        Assert.Contains("4294967295", e.Message);
    }

    // A libmonado with three apps: a game (shown), an overlay, and one that leaves before its name is read.
    private static readonly IntPtr[] AppNames =
        [Marshal.StringToCoTaskMemUTF8("Game"), Marshal.StringToCoTaskMemUTF8("Bar"), Marshal.StringToCoTaskMemUTF8("Gone")];

    private static uint lastBlocks = uint.MaxValue;
    private static uint lastBlocked;
    private static uint lastShown;
    private static int lastSpace = -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Ok(IntPtr root) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ThreeApps(IntPtr root, uint* count)
    {
        *count = 3;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IdAt(IntPtr root, uint index, uint* id)
    {
        *id = 10 + index;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NameOf(IntPtr root, uint id, byte** name)
    {
        if (id == 12)
        {
            return -3;
        }

        *name = (byte*)AppNames[id - 10];
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StateOf(IntPtr root, uint id, uint* flags)
    {
        // The game: shown, session running, inputs blocked. The bar: session running, overlay.
        *flags = id == 10 ? (1u << 0) | (1u << 1) | (1u << 8) : (1u << 1) | (1u << 4);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Show(IntPtr root, uint id)
    {
        lastShown = id;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Blocks(IntPtr root, uint id, uint flags)
    {
        lastBlocked = id;
        lastBlocks = flags;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SpaceOffset(IntPtr root, int space, MndPose* pose)
    {
        lastSpace = space;
        *pose = new MndPose(new MonadoPose(0, 0, 0, 1, 1, 2, 3));
        return 0;
    }

    private static LibmonadoExports WithApps()
    {
        var exports = LibmonadoExports.ForTests();
        exports.RootDestroy = &Destroy;
        exports.UpdateClientList = &Ok;
        exports.GetNumberClients = &ThreeApps;
        exports.GetClientIdAtIndex = &IdAt;
        exports.GetClientName = &NameOf;
        exports.GetClientState = &StateOf;
        exports.SetClientPrimary = &Show;
        return exports;
    }

    [Fact]
    public void Apps_are_listed_with_their_flags_and_one_that_left_is_skipped()
    {
        using var client = new LibmonadoClient(IntPtr.Zero, WithApps(), new IntPtr(1));

        var apps = client.Apps();

        Assert.Equal(
            [
                new MonadoApp(10, "Game", Shown: true, SessionRunning: true, Overlay: false, InputsBlocked: true),
                new MonadoApp(11, "Bar", Shown: false, SessionRunning: true, Overlay: true, InputsBlocked: false),
            ],
            apps);
    }

    [Fact]
    public void Showing_an_app_names_it_to_monado()
    {
        using var client = new LibmonadoClient(IntPtr.Zero, WithApps(), new IntPtr(1));

        client.ShowApp(11);

        Assert.Equal(11u, lastShown);
    }

    [Fact]
    public void Blocking_inputs_sets_only_the_inputs_block_and_unblocking_clears_every_block()
    {
        var exports = WithApps();
        exports.SetClientIoBlocks = &Blocks;
        using var client = new LibmonadoClient(IntPtr.Zero, exports, new IntPtr(1));

        client.BlockInputs(10, blocked: true);
        Assert.Equal((10u, 4u), (lastBlocked, lastBlocks));

        client.BlockInputs(10, blocked: false);
        Assert.Equal((10u, 0u), (lastBlocked, lastBlocks));
    }

    [Fact]
    public void The_stage_offset_is_the_stage_reference_space()
    {
        var exports = LibmonadoExports.ForTests();
        exports.RootDestroy = &Destroy;
        exports.GetReferenceSpaceOffset = &SpaceOffset;
        exports.SetReferenceSpaceOffset = &SpaceOffset;
        using var client = new LibmonadoClient(IntPtr.Zero, exports, new IntPtr(1));

        var offset = client.GetStageOffset();

        Assert.Equal(3, lastSpace);
        Assert.Equal(new MonadoPose(0, 0, 0, 1, 1, 2, 3), offset);
    }

    // MND_ERROR_RECENTERING_NOT_SUPPORTED
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CannotRecentre(IntPtr root) => -5;

    [Fact]
    public void Recentring_the_local_spaces_calls_libmonado_and_its_refusal_is_a_failed_call()
    {
        var exports = LibmonadoExports.ForTests();
        exports.RootDestroy = &Destroy;
        exports.RecenterLocalSpaces = &Ok;
        using var client = new LibmonadoClient(IntPtr.Zero, exports, new IntPtr(1));

        Assert.True(client.OffersLocalRecentre);
        client.RecentreLocalSpaces();

        exports.RecenterLocalSpaces = &CannotRecentre;
        Assert.Throws<MiradorException>(() => client.RecentreLocalSpaces());
    }

    [Fact]
    public void A_libmonado_without_the_newer_functions_offers_none_of_them()
    {
        var exports = LibmonadoExports.ForTests();
        exports.RootDestroy = &Destroy;
        using var client = new LibmonadoClient(IntPtr.Zero, exports, new IntPtr(1));

        Assert.False(client.OffersApps);
        Assert.False(client.OffersInputBlocks);
        Assert.False(client.OffersStageOffset);
        Assert.False(client.OffersLocalRecentre);
        Assert.Empty(client.Apps());
        Assert.Throws<NotSupportedException>(() => client.ShowApp(1));
        Assert.Throws<NotSupportedException>(() => client.BlockInputs(1, true));
        Assert.Throws<NotSupportedException>(() => client.GetStageOffset());
        Assert.Throws<NotSupportedException>(() => client.RecentreLocalSpaces());
    }
}
