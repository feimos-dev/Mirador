using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Tests.Monado;

public class LibmonadoClientTests
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
}
