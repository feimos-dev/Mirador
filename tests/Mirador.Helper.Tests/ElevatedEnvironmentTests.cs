using Mirador.Helper.ElevatedMonado;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class ElevatedEnvironmentTests
{
    private static readonly UserFolders Folders = new(
        UserProfile: @"C:\Users\finn",
        AppData: @"C:\Users\finn\AppData\Roaming",
        LocalAppData: @"C:\Users\finn\AppData\Local",
        Temp: @"C:\Users\finn\AppData\Local\Temp");

    private const string ServerDir = @"C:\Program Files\Mirador\monado\server";
    private const string SteamVrPath = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR";

    private static ElevatedStart Start(
        int lh = 3000, int scale = 100, int comp = 6, string module = "", string directMode = "") =>
        new(lh, scale, comp, module, DirectMode: directMode);

    private static IReadOnlyDictionary<string, string> Build(ElevatedStart? start = null, string module = "", string directMode = "") =>
        ElevatedEnvironment.Build(start ?? Start(module: module, directMode: directMode), Folders, ServerDir, SteamVrPath);

    [Fact]
    public void Approved_variables_come_last_and_win()
    {
        var plain = Build(module: PimaxEnvironment.ModuleId);
        var env = ElevatedEnvironment.Build(
            Start(module: PimaxEnvironment.ModuleId), Folders, ServerDir, SteamVrPath,
            approvedVariables: [new("XRT_LOG", "debug"), new("PIMAX_P3C_LOG", "debug")]);

        Assert.Equal("debug", env["XRT_LOG"]);
        Assert.Equal("debug", env["PIMAX_P3C_LOG"]);
        Assert.Equal(plain.Count + 2, env.Count);
        Assert.Equal(["XRT_LOG", "PIMAX_P3C_LOG"], env.Keys.TakeLast(2));
    }

    [Fact]
    public void A_duplicate_name_keeps_the_last_value()
    {
        var env = ElevatedEnvironment.Build(Start(), Folders, ServerDir, SteamVrPath, approvedVariables: [new("PIMAX_P3C_LOG", "info"), new("pimax_p3c_log", "debug")]);

        Assert.Equal("debug", env["PIMAX_P3C_LOG"]);
    }

    [Fact]
    public void A_refused_name_never_reaches_the_environment()
    {
        Assert.Throws<ArgumentException>(() =>
            ElevatedEnvironment.Build(Start(), Folders, ServerDir, SteamVrPath, approvedVariables: [new("Path", @"C:\Users\you\bin")]));
    }

    [Fact]
    public void Headless_adds_the_null_compositor_and_the_simulated_headset_and_nothing_else()
    {
        var plain = Build();
        var headless = Build(Start() with { Headless = true });

        Assert.False(plain.ContainsKey("XRT_COMPOSITOR_NULL"));
        Assert.False(plain.ContainsKey("SIMULATED_ENABLE"));
        Assert.Equal("1", headless["XRT_COMPOSITOR_NULL"]);
        Assert.Equal("1", headless["SIMULATED_ENABLE"]);
        Assert.Equal(plain.Count + 2, headless.Count);
    }

    [Fact]
    public void Headless_with_a_headset_module_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Build(Start(module: PimaxEnvironment.ModuleId) with { Headless = true }));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void The_relaxed_pipe_only_without_the_elevation_fix_or_under_administrator_protection(bool fix, bool protection, bool relaxed)
    {
        var env = ElevatedEnvironment.Build(Start() with { ElevationFix = fix }, Folders, ServerDir, SteamVrPath, protection);

        Assert.Equal(relaxed, env.TryGetValue("IPC_RELAXED_CONNECTION_SECURITY", out var value));
        if (relaxed)
        {
            Assert.Equal("1", value);
        }
    }

    [Fact]
    public void Standby_on_exit_only_when_asked()
    {
        Assert.Equal("1", Build(Start() with { StandbyOnExit = true })["LH_STANDBY_ON_EXIT"]);
        Assert.False(Build().ContainsKey("LH_STANDBY_ON_EXIT"));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    [InlineData("2", false)]
    public void Administrator_protection_is_mode_two(object? value, bool on) =>
        Assert.Equal(on, AdministratorProtection.IsOn(value));

    [Fact]
    public void No_user_path()
    {
        var env = Build();

        var system32 = Environment.SystemDirectory;
        var expected = $"{system32};{Path.GetDirectoryName(system32)};{Path.Combine(system32, "wbem")};{ServerDir}";
        Assert.Equal(expected, env["PATH"]);
        Assert.DoesNotContain(Folders.UserProfile, env["PATH"]);
    }

    [Fact]
    public void Users_temp_for_the_pipe_name()
    {
        var env = Build();

        Assert.Equal(Folders.Temp, env["TEMP"]);
        Assert.Equal(Folders.Temp, env["TMP"]);

        Assert.Equal(
            MonadoPipeName.Prefix + Folders.Temp + @"\" + MonadoPipeName.Suffix,
            MonadoPipeName.NameFor(env, AnsiCodePage.RoundTrip));
    }

    [Fact]
    public void Temp_and_pipe_name_come_from_the_users_block()
    {
        var block = new Dictionary<string, string>
        {
            ["USERPROFILE"] = @"C:\Users\finn",
            ["APPDATA"] = @"C:\Users\finn\AppData\Roaming",
            ["LOCALAPPDATA"] = @"C:\Users\finn\AppData\Local",
            ["TEMP"] = @"C:\Users\finn\AppData\Local\Temp",
            ["TMP"] = @"C:\Users\finn\AppData\Local\Temp",
            ["SystemRoot"] = @"C:\WINDOWS",
        };

        var folders = UserFolders.FromBlock(block);
        var env = ElevatedEnvironment.Build(Start(), folders, ServerDir, SteamVrPath);

        Assert.Equal(block["TEMP"], env["TEMP"]);
        Assert.Equal(block["TMP"], env["TMP"]);
        Assert.Equal(block["USERPROFILE"], env["USERPROFILE"]);
        Assert.Equal(block["APPDATA"], env["APPDATA"]);
        Assert.Equal(block["LOCALAPPDATA"], env["LOCALAPPDATA"]);

        var name = MonadoPipeName.NameFor(env, AnsiCodePage.RoundTrip);
        Assert.Equal(MonadoPipeName.NameFor(block, AnsiCodePage.RoundTrip), name);
        Assert.Equal(@"\\.\pipe\C:\Users\finn\AppData\Local\Temp\/monado_comp_ipc", name);
        Assert.DoesNotContain(@"C:\Windows", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Temp_follows_the_blocks_order()
    {
        var both = UserFolders.FromBlock(new Dictionary<string, string>
        {
            ["USERPROFILE"] = @"C:\Users\u",
            ["temp"] = @"D:\TempB",
            ["tmp"] = @"D:\TempA",
        });
        Assert.Equal(@"D:\TempA", both.Temp);

        var temp = new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\u", ["TEMP"] = @"D:\TempB", ["TMP"] = "" };
        Assert.Equal(@"D:\TempB", UserFolders.FromBlock(temp).Temp);
        Assert.Equal(
            MonadoPipeName.NameFor(temp, AnsiCodePage.RoundTrip),
            MonadoPipeName.NameFor(ElevatedEnvironment.Build(Start(), UserFolders.FromBlock(temp), ServerDir, SteamVrPath), AnsiCodePage.RoundTrip));

        var none = UserFolders.FromBlock(new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\u" });
        Assert.Equal(@"C:\Users\u", none.Temp);
        Assert.Equal(@"C:\Users\u\AppData\Local", none.LocalAppData);
    }

    [Theory]
    [InlineData(500, 100, 6)]
    [InlineData(1500, 100, 6)]
    [InlineData(3000, 999, 6)]
    [InlineData(3000, 102, 6)]
    [InlineData(3000, 100, 0)]
    [InlineData(3000, 100, 21)]
    public void Settings_out_of_range_refused(int lh, int scale, int comp) =>
        Assert.Throws<ArgumentException>(() => Build(Start(lh, scale, comp)));

    [Fact]
    public void Unknown_module_refused() =>
        Assert.Throws<ArgumentException>(() => Build(Start(module: "vive")));

    [Fact]
    public void Unknown_direct_mode_refused() =>
        Assert.Throws<ArgumentException>(() => Build(directMode: "amd"));

    [Fact]
    public void A_headless_monado_refuses_a_direct_mode_layer() =>
        Assert.Throws<ArgumentException>(() => Build(Start(directMode: DirectModeEnvironment.Nvidia) with { Headless = true }));

    [Fact]
    public void The_nvidia_layers_table_is_exactly_its_five_variables()
    {
        var expected = new Dictionary<string, string>
        {
            ["MIRADOR_DIRECT_MODE"] = "1",
            ["DISABLE_RTSS_LAYER"] = "1",
            ["VK_LOADER_LAYERS_ALLOW"] = "VK_LAYER_MIRADOR_direct_mode",
            ["XRT_COMPOSITOR_FORCE_VK_DISPLAY"] = "0",
            ["XRT_COMPOSITOR_USE_PRESENT_WAIT"] = "1",
        };

        Assert.Equal(expected, DirectModeEnvironment.Variables.ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(5, DirectModeEnvironment.Variables.Count);
    }

    [Fact]
    public void Nvidia_direct_mode_adds_the_layers_table_whatever_the_module()
    {
        foreach (var module in new[] { "", PimaxEnvironment.ModuleId })
        {
            var env = Build(module: module, directMode: DirectModeEnvironment.Nvidia);

            foreach (var (name, value) in DirectModeEnvironment.Variables)
            {
                Assert.Equal(value, env[name]);
            }
        }

        Assert.False(Build(module: PimaxEnvironment.ModuleId).ContainsKey("MIRADOR_DIRECT_MODE"));
    }

    [Fact]
    public void No_vk_or_xr_variables_but_layers_allow()
    {
        var env = Build(module: PimaxEnvironment.ModuleId, directMode: DirectModeEnvironment.Nvidia);

        var vk = env.Keys.Where(k => k.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(["VK_LOADER_LAYERS_ALLOW"], vk);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("XR_", StringComparison.OrdinalIgnoreCase));

        var plain = Build();
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("VK_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("XR_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Relaxed_connection_security_set() =>
        Assert.Equal("1", Build()["IPC_RELAXED_CONNECTION_SECURITY"]);

    [Fact]
    public void Steamvr_path_is_set()
    {
        var env = Build();

        Assert.Equal(SteamVrPath, env["STEAMVR_PATH"]);
    }

    [Fact]
    public void Settings_are_written()
    {
        var env = Build(Start(lh: 5000, scale: 150, comp: 11));

        Assert.Equal("5000", env["LH_DISCOVER_WAIT_MS"]);
        Assert.Equal("150", env["XRT_COMPOSITOR_SCALE_PERCENTAGE"]);
        Assert.Equal("11", env["U_PACING_COMP_MIN_TIME_MS"]);
        Assert.False(env.ContainsKey("IPC_EXIT_ON_DISCONNECT"));
    }

    [Fact]
    public void Users_folders_are_set_but_system_folders_are_the_machines()
    {
        var env = Build();

        Assert.Equal(Folders.UserProfile, env["USERPROFILE"]);
        Assert.Equal(Folders.AppData, env["APPDATA"]);
        Assert.Equal(Folders.LocalAppData, env["LOCALAPPDATA"]);
        Assert.False(string.IsNullOrEmpty(env["SystemRoot"]));
        Assert.False(string.IsNullOrEmpty(env["ProgramData"]));
    }
}
