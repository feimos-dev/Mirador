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
    [InlineData("LH_LOG")]
    [InlineData("XRT_LOG")]
    public void Ordinary_names_are_allowed(string name) => Assert.False(ElevatedVariables.IsRefused(name));

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("A\0B")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("ProgramFiles(x86)")]
    public void Only_plain_ascii_names_are_allowed(string name) => Assert.True(ElevatedVariables.IsRefused(name));

    [Theory]
    [InlineData("IPC_RELAXED_CONNECT\uFF29ON_SECUR\uFF29TY")] // fullwidth I
    [InlineData("ZZFOLD_\uFF29_X")]
    [InlineData("LH_\u212AEEP")] // Kelvin sign
    [InlineData("STEAMVR_PATH\u212A")]
    [InlineData("P\u0130MAX_LOG")] // dotted capital I
    [InlineData("P\u0131MAX_LOG")] // dotless small i
    public void Look_alike_letters_are_refused(string name) => Assert.True(ElevatedVariables.IsRefused(name));

    [Theory]
    [InlineData("STEAMVR_PATH")]
    [InlineData("SystemRoot")]
    [InlineData("WINDIR")]
    [InlineData("SystemDrive")]
    [InlineData("ComSpec")]
    [InlineData("PATHEXT")]
    [InlineData("ProgramFiles")]
    [InlineData("ProgramW6432")]
    [InlineData("ProgramData")]
    [InlineData("CommonProgramFiles")]
    [InlineData("CommonProgramW6432")]
    [InlineData("STEAMVR_LH_ENABLE")]
    [InlineData("XDG_RUNTIME_DIR")]
    [InlineData("xdg_cache_home")]
    [InlineData("VK_IMPLICIT_LAYER_PATH")]
    [InlineData("VK_ADD_IMPLICIT_LAYER_PATH")]
    public void Names_the_helper_owns_are_refused(string name) => Assert.True(ElevatedVariables.IsRefused(name));

    [Fact]
    public void The_fixed_tables_and_monados_settings_are_refused()
    {
        Assert.All(DreamAirEnvironment.ModuleVariables, v => Assert.True(ElevatedVariables.IsRefused(v.Key), v.Key));
        Assert.All(DirectModeEnvironment.Variables, v => Assert.True(ElevatedVariables.IsRefused(v.Key), v.Key));
        Assert.All(HeadlessEnvironment.Variables, v => Assert.True(ElevatedVariables.IsRefused(v.Key), v.Key));
        foreach (var name in new[]
                 {
                     MonadoSettingsEnvironment.LighthouseWait, MonadoSettingsEnvironment.RenderScale, MonadoSettingsEnvironment.CompositorMinTime,
                     MonadoSettingsEnvironment.ExitOnDisconnect, MonadoSettingsEnvironment.StandbyOnExit,
                 })
        {
            Assert.True(ElevatedVariables.IsRefused(name), name);
        }
    }

    /// <summary>
    /// Every name the helper sets itself, in any start it builds: with and without a module, headless, with every
    /// setting on and without the elevation fix (so the relaxed connection security is there too).
    /// </summary>
    [Fact]
    public void Every_name_the_helper_sets_itself_is_refused()
    {
        var folders = new Mirador.Helper.ElevatedMonado.UserFolders(@"C:\Users\you", @"C:\Users\you\AppData\Roaming", @"C:\Users\you\AppData\Local", @"C:\Users\you\AppData\Local\Temp");
        var starts = new[]
        {
            new ElevatedStart(3000, 100, 6, true, PimaxEnvironment.ModuleId, StandbyOnExit: true),
            new ElevatedStart(3000, 100, 6, true, "", Headless: true, StandbyOnExit: true),
        };

        var names = starts
            .SelectMany(start => Mirador.Helper.ElevatedMonado.ElevatedEnvironment.Build(start, folders, @"C:\Program Files\Mirador\monado\server", @"C:\SteamVR").Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("STEAMVR_PATH", names);
        Assert.Contains("IPC_RELAXED_CONNECTION_SECURITY", names);
        Assert.All(names, name => Assert.True(ElevatedVariables.IsRefused(name), name));
    }

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
