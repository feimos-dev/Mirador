using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class ElevatedVariablesTests
{
    [Theory]
    [InlineData("path")]
    [InlineData("Temp")]
    [InlineData("tmp")]
    [InlineData("LocalAppData")]
    [InlineData("xr_runtime_json")]
    [InlineData("XR_API_LAYER_PATH")]
    [InlineData("VK_LOADER_LAYERS_ENABLE")]
    [InlineData("vk_icd_filenames")]
    [InlineData("ipc_relaxed_connection_security")]
    public void Refused_names_ignore_case(string name) => Assert.True(ElevatedVariables.IsRefused(name));

    [Theory]
    [InlineData("PIMAX_P3C_LOG")]
    [InlineData("XRT_COMPOSITOR_DESIRED_MODE")]
    [InlineData("STEAMVR_LH_ENABLE")]
    [InlineData("XRT_LOG")]
    public void Ordinary_names_are_allowed(string name) => Assert.False(ElevatedVariables.IsRefused(name));

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("A\0B")]
    public void A_name_windows_cant_hold_is_refused(string name) => Assert.True(ElevatedVariables.IsRefused(name));

    [Fact]
    public void Every_cleared_name_is_refused() =>
        Assert.All(DirectModeEnvironment.Cleared, name => Assert.Contains(name, ElevatedVariables.RefusedNames));

    [Fact]
    public void The_first_refused_name_is_named()
    {
        Assert.Null(ElevatedVariables.FirstRefused([new("PIMAX_P3C_LOG", "info")]));
        Assert.Equal("Path", ElevatedVariables.FirstRefused([new("PIMAX_P3C_LOG", "info"), new("Path", @"C:\x"), new("TEMP", @"C:\t")]));
    }
}
