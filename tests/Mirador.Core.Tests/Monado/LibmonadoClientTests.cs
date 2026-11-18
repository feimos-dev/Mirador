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
}
