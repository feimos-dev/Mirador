using Microsoft.Extensions.Time.Testing;
using Mirador.Core;
using Mirador.Core.Elevation;
using Mirador.Core.Lighthouse;
using Mirador.Core.Monado;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Pimax.Tests;

public sealed class PimaxModuleTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDir dir = new();
    private readonly List<string> steps = [];
    private readonly FakeHelper helper;
    private readonly FakeUsbDevices usb = new(DreamAirIds.Board, DreamAirIds.EyeTracker, DreamAirIds.Receiver);
    private readonly FakeProcesses processes = new();
    private readonly TimerWatch time = new(new FakeTimeProvider());
    private readonly FakeConsoleStarter consoles;
    private readonly AppPaths paths;
    private readonly DreamAirStateStore state;
    private readonly List<IReadOnlyDictionary<string, string>> saves = [];
    private Action? duringChecks;

    public PimaxModuleTests()
    {
        helper = new FakeHelper(steps, processes);

        processes.Add("PimaxClient", 7);
        paths = new AppPaths(Path.Combine(dir.Path, "Mirador"));
        state = new DreamAirStateStore(paths.DreamAirFile);

        var pimax = new PimaxInstall(Path.Combine(dir.Path, "Pimax", "Runtime"), "2.0.4.329");
        Directory.CreateDirectory(Path.GetDirectoryName(pimax.BlobPath("K11_RAWH"))!);
        File.WriteAllText(pimax.TobiiRuntimeExe, "exe");
        File.WriteAllText(pimax.BlobPath("K11_RAWH"), "blob");
        Registry = new FakeRegistry().Set(
            @"HKLM\SYSTEM\CurrentControlSet\Services\PiServiceLauncher", "ImagePath",
            $"\"{Path.Combine(pimax.RuntimeDir, "PiServiceLauncher.exe")}\"");

        dir.Touch("SteamVR", "drivers", "lighthouse", "bin", "win64", "driver_lighthouse.dll");

        consoles = new FakeConsoleStarter(spec =>
        {
            steps.Add("upload");
            var runtime = new FakeConsole("Opened device 'XR5DA-1'", "tobii_xr_create successful");
            runtime.End(0);
            return runtime;
        });
    }

    private FakeRegistry Registry { get; }

    public void Dispose() => dir.Dispose();

    private ModuleContext Context => new(paths, helper, processes, usb, time);

    private PimaxModule Module() => new(new PimaxDeps(
        Registry,
        new GeometryUploader(consoles.Start, usb, processes, paths, time),
        state,
        () => { duringChecks?.Invoke(); return Path.Combine(dir.Path, "SteamVR"); },
        _ => EyeBlobVariants.Fallback,
        () =>
        {
            usbQueries++;
            return usb.Present();
        }));

    private int usbQueries;

    private static Profile DreamAirProfile(string? layerFolder = null) => new(
        "p1", "Dream Air", PimaxModule.ModuleId, BuildSource.Mirador, [],
        layerFolder is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["layerFolder"] = layerFolder });

    private Task<ModuleSession> BeforeStart(Profile? profile = null, CancellationToken ct = default) =>
        Module().BeforeStartAsync(Context, profile ?? DreamAirProfile(), new Progress(steps), Save, ct).WaitAsync(Limit);

    private void Save(IReadOnlyDictionary<string, string> data)
    {
        saves.Add(data);
        steps.Add("save " + string.Join(",", data.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Fact]
    public async Task BeforeStart_stops_pimax_and_returns_the_dream_airs_own_environment()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true);

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Equal(DirectModeKind.Nvidia, Module().DefaultDirectMode);
        Assert.Equal(
            new EnvVar[] { new("STEAMVR_LH_ENABLE", "1") },
            session.Environment);
        Assert.Empty(session.RecoveryData);
        Assert.Equal(
            new[] { "Checking the computer…", "Stopping Pimax's software…", "StopPimax", "Uploading the eye geometry…", "upload" },
            steps);
        Assert.Equal(new StatusLine("eye", "Eye tracker", StatusLevel.Ok, "Geometry uploaded"), session.Status.Single(l => l.Key == "eye"));
    }

    [Fact]
    public async Task With_nothing_of_pimax_running_start_needs_no_helper()
    {
        processes.Remove("PimaxClient");
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service isn't running.") { NotDelivered = true };

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Empty(helper.Sent);
        Assert.Contains("upload", steps);
    }

    [Theory]
    [InlineData("PiServiceLauncher")]
    [InlineData("pi_server")]
    [InlineData("VRSS_GAZE_PROVIDER")]
    public async Task Any_of_pimax_running_asks_the_helper(string name)
    {
        processes.Remove("PimaxClient");
        processes.Add(name);
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service isn't running.") { NotDelivered = true };

        var ex = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("Mirador's helper service isn't running.", ex.Message);
        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
    }

    [Fact]
    public async Task A_layer_folder_left_in_a_profile_is_ignored()
    {
        var profile = DreamAirProfile(Path.Combine(dir.Path, "gone", "layer-build"));

        using var session = (DreamAirSession)await BeforeStart(profile);

        Assert.Equal(PimaxModule.FixedEnvironment, session.Environment);
        Assert.Null(ElevatedChoice.RunsAsYouReason(profile, Module(), installedCopy: true));
    }

    [Fact]
    public async Task Failed_check_refuses_without_touching_pimax()
    {
        File.Delete(Path.Combine(dir.Path, "SteamVR", "drivers", "lighthouse", "bin", "win64", "driver_lighthouse.dll"));

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Contains("SteamVR", e.Message);
        Assert.Empty(helper.Sent);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task Missing_helper_refuses_with_its_message()
    {
        const string NotRunning = "Mirador's helper service isn't running. Reinstall Mirador; for a dev build, run tools\\dev-install-helper.ps1 as administrator.";
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, NotRunning) { NotDelivered = true };
        processes.Add("PiServiceLauncher");

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal(NotRunning, e.Message);
        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task A_stop_with_no_answer_refuses_and_leaves_pimax_as_it_is()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service didn't answer in time.");
        processes.Add("PiServiceLauncher");

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("Mirador's helper service didn't answer in time.", e.Message);
        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task Stop_during_the_checks_doesnt_touch_pimax()
    {
        using var cts = new CancellationTokenSource();
        duringChecks = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BeforeStart(ct: cts.Token));

        Assert.Empty(helper.Sent);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task A_pimax_that_wouldnt_stop_refuses_the_start()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Pimax's service didn't stop within 20 s.");

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("Pimax's service didn't stop within 20 s.", e.Message);
        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
    }

    [Fact]
    public async Task Failed_geometry_upload_is_a_warning_not_a_refusal()
    {
        usb.Ids.Remove(DreamAirIds.EyeTracker);

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Equal(
            new StatusLine("eye", "Eye tracker", StatusLevel.Warning, "The eye tracker isn't on USB. Is the headset connected and switched on?"),
            session.Status.Single(l => l.Key == "eye"));
    }

    [Fact]
    public async Task Stop_while_starting_lets_the_cancel_out_and_leaves_pimax_stopped()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true);
        using var cts = new CancellationTokenSource();
        helper.OnSend = command =>
        {
            if (command == HelperCommand.StopPimax)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BeforeStart(ct: cts.Token));

        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
        Assert.DoesNotContain("upload", steps);
    }

    [Fact]
    public async Task End_leaves_pimax_stopped()
    {
        using (var session = (DreamAirSession)await BeforeStart())
        {
            await session.EndAsync(CancellationToken.None);
        }

        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
    }

    [Fact]
    public async Task Pimax_warning_is_raised_once_per_appearance()
    {
        using var session = (DreamAirSession)await BeforeStart();
        var warnings = new List<SessionWarning>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };
        processes.Add("pi_server");

        Poll(3);
        lock (warnings)
        {
            Assert.Single(warnings);
        }

        processes.Remove("pi_server");
        Poll(1);
        processes.Add("PiServiceLauncher");
        Poll(2);

        lock (warnings)
        {
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Equal(new SessionWarning("pimax", DreamAirSession.PimaxStarted), w));
        }
    }

    [Fact]
    public async Task Steamvr_starting_while_monado_runs_is_a_warning()
    {
        using var session = (DreamAirSession)await BeforeStart();
        var warnings = new List<SessionWarning>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };
        processes.Add("vrserver");

        Poll(2);

        lock (warnings)
        {
            Assert.Equal([new SessionWarning("lighthouse", DreamAirSession.SteamVrStarted)], warnings);
        }
    }

    [Fact]
    public async Task Recover_leaves_pimax_alone_even_for_an_older_sessions_data()
    {
        await Module().RecoverAsync(Context, new Dictionary<string, string> { ["pimaxWasRunning"] = "true" }, CancellationToken.None);
        await Module().RecoverAsync(Context, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Empty(helper.Sent);
    }

    [Fact]
    public async Task Checks_are_the_pimax_ones()
    {
        var results = await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.Equal(["Pimax Play", "Eye geometry", "SteamVR", "Headset", "Eye tracker variant"], results.Select(r => r.Label));
        Assert.All(results.SkipLast(1), r => Assert.Equal(StatusLevel.Ok, r.Level));
        Assert.Equal(StatusLevel.Warning, results[^1].Level);
    }

    [Fact]
    public async Task A_known_variant_needs_no_warning()
    {
        state.Save(new DreamAirState(BlobVariant: "K11_RAWH"));

        var results = await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.DoesNotContain(results, r => r.Label == "Eye tracker variant");
    }

    private static Profile WithVariant(string variant) =>
        DreamAirProfile() with { ModuleOptions = new Dictionary<string, string> { [EyeVariantChoice.OptionKey] = variant } };

    [Fact]
    public async Task A_profile_with_no_eye_variant_uploads_nothing_and_needs_no_pimax_play()
    {
        Registry.Set(@"HKLM\SYSTEM\CurrentControlSet\Services\PiServiceLauncher", "ImagePath", "");
        var profile = WithVariant(EyeVariantChoice.None);

        var results = await Module().CheckAsync(Context, profile, CancellationToken.None);
        using var session = (DreamAirSession)await BeforeStart(profile);

        Assert.DoesNotContain(results, r => r.Label is "Eye geometry" or "Eye tracker variant");
        Assert.DoesNotContain("upload", steps);
        Assert.DoesNotContain(session.Status, l => l.Key == DreamAirLogParser.EyeKey);
    }

    [Fact]
    public async Task A_profiles_own_variant_is_uploaded_without_a_warning()
    {
        File.WriteAllText(new PimaxInstall(Path.Combine(dir.Path, "Pimax", "Runtime"), null).BlobPath("K10_RAYH"), "blob");
        var profile = WithVariant("K10_RAYH");

        var results = await Module().CheckAsync(Context, profile, CancellationToken.None);
        using var session = (DreamAirSession)await BeforeStart(profile);

        Assert.DoesNotContain(results, r => r.Label == "Eye tracker variant");
        Assert.Contains(consoles.Specs, spec => spec.Arguments.Contains("config_XR5PIMAXDA_K10_RAYH.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_profiles_own_variant_isnt_called_a_different_headset()
    {
        state.Save(new DreamAirState(BlobVariant: "K10_RAYH", BlobTrackerSerial: "XR5DA-0"));
        using var session = (DreamAirSession)await BeforeStart(WithVariant("K11_RAWH"));
        var warnings = new List<SessionWarning>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };

        Poll(1);

        lock (warnings)
        {
            Assert.Empty(warnings);
        }
    }

    [Fact]
    public async Task A_profiles_own_variant_uploads_nothing_to_a_headset_without_an_eye_tracker()
    {
        Plug(PimaxModels.CrystalLight);

        using var session = (DreamAirSession)await BeforeStart(WithVariant("K11_RAWH"));

        Assert.DoesNotContain("upload", steps);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("K12_RAWH")]
    [InlineData("")]
    public void An_eye_variant_mirador_doesnt_know_is_automatic(string value) =>
        Assert.Null(EyeVariantChoice.Of(WithVariant(value)));

    [Fact]
    public async Task Checks_read_the_variant_pimax_runs_with_and_remember_it()
    {
        processes.Add(PimaxNames.EyeRuntimeProcess);
        helper.Replies[HelperCommand.GetPimaxEyeBlob] = new HelperReply(true, Value: "K10_RAYH");

        await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.Equal("K10_RAYH", state.Load().BlobVariant);
    }

    [Fact]
    public async Task Without_pimaxs_eye_runtime_the_helper_isnt_asked()
    {
        await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.DoesNotContain(HelperCommand.GetPimaxEyeBlob, helper.Sent);
    }

    [Fact]
    public async Task A_different_tracker_clears_the_variant_and_says_so_at_the_first_look()
    {
        state.Save(new DreamAirState(BlobVariant: "K10_RAYH", BlobTrackerSerial: "XR5DA-0"));
        using var session = (DreamAirSession)await BeforeStart();
        var warnings = new List<SessionWarning>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };

        Poll(1);

        Assert.Equal((null, "XR5DA-1"), (state.Load().BlobVariant, state.Load().BlobTrackerSerial));
        lock (warnings)
        {
            Assert.Equal([new SessionWarning("geometry", PimaxModule.DifferentHeadset)], warnings);
        }
    }

    [Fact]
    public async Task Freeing_the_display_stops_pimax_only_while_it_runs()
    {
        await Module().FreeDisplayAsync(Context, CancellationToken.None);
        await Module().FreeDisplayAsync(Context, CancellationToken.None);

        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
    }

    [Fact]
    public async Task Freeing_the_display_says_why_pimax_wouldnt_stop()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service isn't running.") { NotDelivered = true };

        var e = await Assert.ThrowsAsync<MiradorException>(() => Module().FreeDisplayAsync(Context, CancellationToken.None));

        Assert.Equal("Mirador's helper service isn't running.", e.Message);
    }

    [Fact]
    public void The_headset_is_present_by_its_board_or_its_eye_tracker()
    {
        var module = Module();

        Assert.True(module.IsPresent(new HashSet<UsbId> { DreamAirIds.Board }));
        Assert.True(module.IsPresent(new HashSet<UsbId> { DreamAirIds.EyeTracker }));
        Assert.False(module.IsPresent(new HashSet<UsbId> { DreamAirIds.Receiver }));
        Assert.True(module.UsesSteamVrLighthouse);
        Assert.Equal(("pimax", "Pimax"), (module.Id, module.DisplayName));
    }

    private void Poll(int times)
    {
        for (var i = 0; i < times; i++)
        {
            time.Clock.Advance(DreamAirSession.PollInterval);
        }
    }

    [Fact]
    public async Task A_monado_with_no_driver_for_the_headset_asks_to_stop_at_once_with_that_reason()
    {
        var session = (DreamAirSession)await BeforeStart();
        var reasons = new List<string>();
        session.StopRequested += reasons.Add;

        session.OnServiceLine(" WARN [p_dev_get_usb_device] No builder can be used to create a head device");
        session.OnServiceLine(" INFO [something] more output");

        var reason = Assert.Single(reasons);
        Assert.StartsWith("This Monado has no driver for the", reason);
        session.Dispose();
    }

    [Fact]
    public async Task Ending_stops_the_watch()
    {
        var session = (DreamAirSession)await BeforeStart();
        var warnings = 0;
        session.Warning += _ => Interlocked.Increment(ref warnings);
        await session.EndAsync(CancellationToken.None);
        processes.Add("pi_server");

        Poll(2);

        Assert.Equal(0, warnings);
    }

    private sealed class Progress(List<string> steps) : IProgress<string>
    {
        public void Report(string value) => steps.Add(value);
    }

    private sealed class FakeHelper(List<string> steps, FakeProcesses processes) : IHelperClient
    {
        public Dictionary<HelperCommand, HelperReply> Replies { get; } = [];

        public List<HelperCommand> Sent { get; } = [];

        public Action<HelperCommand>? OnSend { get; set; }

        public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(command);
            steps.Add(command.ToString());
            OnSend?.Invoke(command);
            var reply = Replies.GetValueOrDefault(command) ?? new HelperReply(true);
            if (command == HelperCommand.StopPimax && reply.Ok)
            {
                processes.Remove("PimaxClient");
            }

            return Task.FromResult(reply);
        }
    }

    private void Plug(PimaxModel model)
    {
        usb.Ids.Clear();
        usb.Ids.Add(model.Board);
        if (model.Lighthouse)
        {
            usb.Ids.Add(DreamAirIds.Receiver);
        }
    }

    [Theory]
    [InlineData(0x0018)]
    [InlineData(0x0040)]
    [InlineData(0x0042)]
    [InlineData(0x0044)]
    public void Every_pimax_board_is_its_headset(int pid)
    {
        Assert.True(Module().IsPresent(new HashSet<UsbId> { new(0x34A4, (ushort)pid) }));
    }

    [Fact]
    public async Task A_crystal_light_gets_pimax_stopped_and_direct_mode_but_no_eye_or_lighthouse_steps()
    {
        Plug(PimaxModels.CrystalLight);
        var module = Module();

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Contains("StopPimax", steps);
        Assert.DoesNotContain("upload", steps);
        Assert.Empty(session.Environment);
        Assert.False(module.UsesSteamVrLighthouse);
        Assert.Equal(DirectModeKind.Nvidia, module.DefaultDirectMode);
        Assert.Equal("", module.ElevatedModuleId);
    }

    [Fact]
    public async Task A_dream_air_se_gets_steamvrs_lighthouse_driver_but_no_eye_steps()
    {
        Plug(PimaxModels.DreamAirSe);

        using var session = (DreamAirSession)await BeforeStart();

        Assert.DoesNotContain("upload", steps);
        Assert.Equal([new EnvVar("STEAMVR_LH_ENABLE", "1")], session.Environment);
        Assert.True(Module().UsesSteamVrLighthouse);
    }

    [Fact]
    public void The_dream_air_runs_elevated_with_the_helpers_pimax_table()
    {
        Assert.Equal("pimax", Module().ElevatedModuleId);
        Assert.True(Module().UsesSteamVrLighthouse);
    }

    [Fact]
    public async Task An_untested_model_is_checked_with_a_warning_saying_so()
    {
        Plug(PimaxModels.CrystalSuper);

        var checks = await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None).WaitAsync(Limit);

        var warning = Assert.Single(checks, c => c.Label == "Headset" && c.Level == StatusLevel.Warning && c.Text.Contains("isn't tested", StringComparison.Ordinal));
        Assert.Contains("Pimax Crystal Super", warning.Text);
        Assert.DoesNotContain(checks, c => c.Label is "Eye geometry" or "SteamVR");
    }

    [Fact]
    public void The_model_is_looked_up_once_and_again_only_when_the_devices_change()
    {
        var module = Module();

        _ = module.UsesSteamVrLighthouse;
        _ = module.ElevatedModuleId;
        _ = module.Model();
        Assert.Equal(1, usbQueries);

        Plug(PimaxModels.CrystalLight);
        module.RefreshModel();
        Assert.Equal(2, usbQueries);
        Assert.False(module.UsesSteamVrLighthouse);
        Assert.Equal(2, usbQueries);
    }

    [Fact]
    public async Task A_start_looks_the_model_up_afresh()
    {
        var module = Module();
        _ = module.Model();
        Plug(PimaxModels.CrystalLight);

        using var session = (DreamAirSession)await module.BeforeStartAsync(Context, DreamAirProfile(), new Progress(steps), Save, CancellationToken.None).WaitAsync(Limit);

        Assert.DoesNotContain("upload", steps);
        Assert.Equal(PimaxModels.CrystalLight, module.Model());
    }

    [Fact]
    public async Task Without_the_headset_plugged_in_monados_missing_head_isnt_blamed_on_its_driver()
    {
        usb.Ids.Remove(DreamAirIds.Board);
        using var session = (DreamAirSession)await BeforeStart();

        session.OnServiceLine("\tNo builder can be used to create a head device");

        var headset = Assert.Single(session.Status, l => l.Key == DreamAirLogParser.HeadsetKey);
        Assert.Equal(StatusLevel.Error, headset.Level);
        Assert.DoesNotContain("no driver", headset.Text);
        Assert.Contains("found no headset", headset.Text);
    }

    [Fact]
    public async Task A_monado_without_a_driver_for_the_headset_says_so_plainly()
    {
        Plug(PimaxModels.CrystalLight);
        using var session = (DreamAirSession)await BeforeStart();

        session.OnServiceLine("\tNo builder can be used to create a head device");

        var headset = Assert.Single(session.Status, l => l.Key == DreamAirLogParser.HeadsetKey);
        Assert.Equal(StatusLevel.Error, headset.Level);
        Assert.Contains("no driver for the Pimax Crystal Light", headset.Text);
    }

    [Fact]
    public void Module_variables_are_only_the_lighthouse_switch()
    {
        Assert.Equal([new KeyValuePair<string, string>("STEAMVR_LH_ENABLE", "1")], PimaxEnvironment.DreamAirVariables);
        Assert.Equal([new EnvVar("STEAMVR_LH_ENABLE", "1")], PimaxModule.EnvironmentFor(PimaxModels.DreamAir));
    }
}
