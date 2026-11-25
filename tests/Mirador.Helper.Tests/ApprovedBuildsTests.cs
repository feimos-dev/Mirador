using System.Text.Json;
using Microsoft.Win32;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

internal sealed class FakeApprovedBuildStore : IApprovedBuildStore
{
    public Dictionary<(string, string), ApprovedBuild> Builds { get; } = [];

    public ApprovedBuild? Get(string owner, string profileId) => Builds.GetValueOrDefault((owner, profileId));

    public void Set(ApprovedBuild build) => Builds[(build.Owner, build.ProfileId)] = build;

    public void Remove(string owner, string profileId) => Builds.Remove((owner, profileId));

    public IReadOnlyList<string> ProfilesOf(string owner) => [.. Builds.Keys.Where(k => k.Item1 == owner).Select(k => k.Item2)];
}

public sealed class ApprovedBuildsTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Finn = "S-1-5-21-1-2-3-1001";
    private const string Other = "S-1-5-21-1-2-3-1002";
    private readonly TempDir temp = new();
    private readonly FakeApprovedBuildStore store = new();
    private readonly string scratch = @"Software\MiradorHelperTests-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        temp.Dispose();
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        hkcu.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
    }

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Variables =
    [
        new("PIMAX_P3C_LOG", "debug"),
        new("XRT_COMPOSITOR_DESIRED_MODE", "a=b=c"),
        new("EMPTY_ONE", ""),
    ];

    private string App => temp.File("app");

    private string MakeInstall(string name = "install", string service = "service v1")
    {
        var folder = temp.File("user", name);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "bin", "openxr_monado.dll"), "dll v1");
        File.WriteAllText(Path.Combine(folder, "bin", "monado-service.exe"), service);
        return folder;
    }

    [Fact]
    public void Approving_copies_the_build_into_the_install_folder_and_records_every_files_hash()
    {
        var source = MakeInstall();

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.Equal(Path.Combine(App, "builds", Finn, "p1", Commit), build.Folder);
        Assert.Equal("service v1", File.ReadAllText(Path.Combine(build.ServerDir, "monado-service.exe")));
        Assert.Equal(3, build.Files.Count);
        Assert.Equal(@"bin\openxr_monado.dll", LibraryPathOf(build.Manifest));
        Assert.Same(build, store.Get(Finn, "p1"));
        Assert.Null(ApprovedBuilds.Verify(build, App));
        Assert.True(ApprovedBuilds.Matches(build, source));
    }

    [Fact]
    public void The_copy_gets_miradors_own_stand_in_whatever_the_build_had_there()
    {
        var source = MakeInstall();
        Directory.CreateDirectory(Path.Combine(source, "bin", "service"));
        File.WriteAllText(Path.Combine(source, "bin", "service", "monado-service.exe"), "the build's own");
        Directory.CreateDirectory(Path.Combine(App, "standin"));
        File.WriteAllText(Path.Combine(App, "standin", "mirador-standin.exe"), "mirador's stand-in");

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.Equal(Path.Combine(build.ServerDir, "service"), build.StandInDir);
        Assert.Equal("mirador's stand-in", File.ReadAllText(Path.Combine(build.StandInDir, "monado-service.exe")));
        Assert.Equal("mirador's stand-in", File.ReadAllText(Path.Combine(build.StandInDir, "mirador-standin.exe")));
        Assert.Null(ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_build_without_a_stand_in_gets_one_in_its_copy()
    {
        Directory.CreateDirectory(Path.Combine(App, "standin"));
        File.WriteAllText(Path.Combine(App, "standin", "mirador-standin.exe"), "mirador's stand-in");

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);

        Assert.Equal("mirador's stand-in", File.ReadAllText(Path.Combine(build.StandInDir, "mirador-standin.exe")));
        Assert.Equal("mirador's stand-in", File.ReadAllText(Path.Combine(build.StandInDir, "monado-service.exe")));
        Assert.Equal(5, build.Files.Count);
    }

    [Fact]
    public void Matches_takes_its_hashes_from_the_function_given()
    {
        var source = MakeInstall();
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);
        var asked = new List<string>();

        Assert.True(ApprovedBuilds.Matches(build, source, f => { asked.Add(f); return ApprovedBuilds.HashOf(f); }));
        Assert.Equal(2, asked.Count);
        Assert.False(ApprovedBuilds.Matches(build, source, _ => "0"));
    }

    [Fact]
    public void A_copy_with_a_file_held_open_is_left_whole_when_forgotten_and_goes_at_the_next_approval()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);

        using (new FileStream(Path.Combine(build.ServerDir, "monado-service.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ApprovedBuilds.Forget(Finn, "p1", App, store);

            Assert.Null(store.Get(Finn, "p1"));
            Assert.True(File.Exists(build.Manifest));
            Assert.True(File.Exists(Path.Combine(build.ServerDir, "openxr_monado.dll")));
        }

        var again = ApprovedBuilds.Approve(Finn, "p1", "abcdef1234", MakeInstall("install2"), App, store);

        Assert.False(Directory.Exists(build.Folder));
        Assert.True(Directory.Exists(again.Folder));
        Assert.Empty(Directory.GetDirectories(Path.Combine(App, "builds"), ".old-*"));
    }

    [Fact]
    public void A_held_copy_of_a_profile_forgotten_for_good_goes_at_another_profiles_approval()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        using (new FileStream(Path.Combine(build.ServerDir, "monado-service.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ApprovedBuilds.Forget(Finn, "p1", App, store);
        }

        Assert.True(Directory.Exists(build.Folder));

        ApprovedBuilds.Approve(Other, "p2", Commit, MakeInstall("install2"), App, store);

        Assert.False(Directory.Exists(Path.Combine(App, "builds", Finn, "p1")));
        Assert.NotNull(store.Get(Other, "p2"));
    }

    [Fact]
    public void A_rebuild_no_longer_matches_and_a_new_approval_replaces_the_old_copy()
    {
        var source = MakeInstall();
        var first = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);
        File.WriteAllText(Path.Combine(source, "bin", "monado-service.exe"), "service v2");

        Assert.False(ApprovedBuilds.Matches(first, source));

        var second = ApprovedBuilds.Approve(Finn, "p1", "abcdef1234", source, App, store);
        Assert.False(Directory.Exists(first.Folder));
        Assert.True(ApprovedBuilds.Matches(second, source));
    }

    [Fact]
    public void A_file_swapped_in_the_approved_copy_is_caught()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(Path.Combine(build.ServerDir, "monado-service.exe"), "evil");

        Assert.Contains("changed since it was approved", ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_dll_added_to_the_approved_copy_is_caught()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(Path.Combine(build.ServerDir, "version.dll"), "evil");

        Assert.Contains("was added", ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_record_naming_another_folder_is_refused()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        var moved = build with { Folder = temp.File("user", "install") };

        Assert.Contains("doesn't name its own folder", ApprovedBuilds.Verify(moved, App));
    }

    [Fact]
    public void A_link_in_the_build_is_refused()
    {
        var source = MakeInstall();
        Directory.CreateDirectory(temp.File("elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(source, "bin", "plugins"), temp.File("elsewhere"));

        var e = Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store));

        Assert.Contains("is a link", e.Message);
        Assert.Null(store.Get(Finn, "p1"));
    }

    [Theory]
    [InlineData("..", Commit)]
    [InlineData("a\\b", Commit)]
    [InlineData("p1", "..\\x")]
    [InlineData("p1", "HEAD")]
    public void Names_that_could_be_paths_are_refused(string profileId, string commit)
    {
        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(Finn, profileId, commit, MakeInstall(), App, store));
    }

    [Fact]
    public void A_build_without_the_service_is_refused()
    {
        var source = MakeInstall();
        File.Delete(Path.Combine(source, "bin", "monado-service.exe"));

        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store));
    }

    [Fact]
    public void A_manifest_naming_a_dll_outside_the_build_is_replaced_by_miradors_own()
    {
        var source = MakeInstall();
        File.WriteAllText(Path.Combine(source, "openxr_monado.json"),
            "{\"runtime\":{\"library_path\":\"" + Path.Combine(source, "bin", "openxr_monado.dll").Replace("\\", "\\\\") + "\"}}");

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.DoesNotContain(source, File.ReadAllText(build.Manifest));
        Assert.Null(ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_manifest_changed_to_name_another_dll_is_caught()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        var json = """{"runtime":{"library_path":"../../evil.dll"}}""";
        File.WriteAllText(build.Manifest, json);
        var rehashed = build with { Files = new Dictionary<string, string>(build.Files) { ["openxr_monado.json"] = ApprovedBuilds.HashOf(build.Manifest) } };

        Assert.Contains("doesn't name its own DLL", ApprovedBuilds.Verify(rehashed, App));
    }

    [Fact]
    public void A_copy_whose_manifest_names_its_dll_with_forward_slashes_must_be_approved_again()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(build.Manifest, """{"runtime":{"library_path":"./bin/openxr_monado.dll"}}""");
        var rehashed = build with { Files = new Dictionary<string, string>(build.Files) { ["openxr_monado.json"] = ApprovedBuilds.HashOf(build.Manifest) } };

        Assert.Contains("Approve the build again", ApprovedBuilds.Verify(rehashed, App));
        Assert.False(ApprovedBuilds.NamesOwnDll(rehashed));
        Assert.True(ApprovedBuilds.NamesOwnDll(ApprovedBuilds.Approve(Finn, "p2", Commit, MakeInstall("other"), App, store)));
    }

    [MonadoInstallFact]
    public void The_copys_manifest_loads_its_dll_as_the_openxr_loader_does()
    {
        var source = Environment.GetEnvironmentVariable(MonadoInstallFactAttribute.Variable)!;

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        var path = Path.GetDirectoryName(build.Manifest) + "\\" + LibraryPathOf(build.Manifest);
        var handle = NativeLoad.LoadLibraryW(path);
        if (handle == IntPtr.Zero && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == NativeLoad.ModNotFound)
        {
            handle = NativeLoad.LoadLibraryExW(path, IntPtr.Zero, NativeLoad.SearchDefaultDirs | NativeLoad.SearchDllLoadDir);
        }

        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        try
        {
            Assert.True(handle != IntPtr.Zero, $"{path} didn't load (error {error}).");
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeLoad.FreeLibrary(handle);
            }
        }
    }

    private static string? LibraryPathOf(string manifest)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        return doc.RootElement.GetProperty("runtime").GetProperty("library_path").GetString();
    }

    [Fact]
    public void Approvals_are_per_user()
    {
        ApprovedBuilds.Approve(Finn, "custom", Commit, MakeInstall(), App, store);
        ApprovedBuilds.Approve(Other, "custom", Commit, MakeInstall("other"), App, store);

        Assert.NotNull(store.Get(Finn, "custom"));
        Assert.NotNull(store.Get(Other, "custom"));
        Assert.True(Directory.Exists(store.Get(Finn, "custom")!.Folder));
        Assert.Null(ApprovedBuilds.Verify(store.Get(Finn, "custom")!, App));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("S-1-5\\..")]
    [InlineData("S-1-5-21-1\n")]
    public void An_owner_that_isnt_a_sid_is_refused(string owner)
    {
        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(owner, "p1", Commit, MakeInstall(), App, store));
    }

    [Fact]
    public void Variables_round_trip_through_the_registry_store()
    {
        var registry = new RegistryApprovedBuildStore(RegistryHive.CurrentUser, scratch);
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, registry, Variables);

        var read = registry.Get(Finn, "p1")!;

        Assert.Equal(Variables, read.Variables);
        Assert.Equal(build.Files, read.Files);

        using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        using (var key = hkcu.OpenSubKey($@"{scratch}\{Finn}\p1", writable: true)!)
        {
            Assert.Equal(RegistryValueKind.MultiString, key.GetValueKind("Variables"));
            key.DeleteValue("Variables");
        }

        Assert.Empty(registry.Get(Finn, "p1")!.Variables);
    }

    [Fact]
    public void A_bundled_approval_has_no_folder_or_files()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", ApprovedBuilds.BundledCommit, "", App, store, Variables);

        Assert.Equal(ApprovedBuilds.BundledCommit, build.Commit);
        Assert.Equal("", build.Folder);
        Assert.Empty(build.Files);
        Assert.Equal(Variables, build.Variables);
        Assert.Same(build, store.Get(Finn, "p1"));
        Assert.False(Directory.Exists(ApprovedBuilds.ProfileRoot(App, Finn, "p1")));
        Assert.NotNull(ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void Approving_variables_alone_keeps_the_copy_and_replaces_only_the_variables()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store, Variables);
        var copied = File.GetLastWriteTimeUtc(build.Manifest);

        var updated = ApprovedBuilds.ApproveVariables(Finn, "p1", App, store, [new("PIMAX_P3C_LOG", "info")]);

        Assert.Equal((build.Commit, build.Folder), (updated.Commit, updated.Folder));
        Assert.Equal(build.Files.OrderBy(f => f.Key), updated.Files.OrderBy(f => f.Key));
        Assert.Equal([new KeyValuePair<string, string>("PIMAX_P3C_LOG", "info")], updated.Variables);
        Assert.Same(updated, store.Get(Finn, "p1"));
        Assert.Equal(copied, File.GetLastWriteTimeUtc(build.Manifest));
        Assert.Null(ApprovedBuilds.Verify(updated, App));
    }

    [Fact]
    public void Approving_variables_alone_works_for_the_bundled_monado()
    {
        ApprovedBuilds.Approve(Finn, "p1", ApprovedBuilds.BundledCommit, "", App, store, Variables);

        var updated = ApprovedBuilds.ApproveVariables(Finn, "p1", App, store, []);

        Assert.Equal(ApprovedBuilds.BundledCommit, updated.Commit);
        Assert.Empty(updated.Variables);
    }

    [Fact]
    public void Approving_variables_alone_needs_an_approval_whose_copy_is_intact()
    {
        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.ApproveVariables(Finn, "p1", App, store, Variables));

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(Path.Combine(build.ServerDir, "monado-service.exe"), "evil");
        var e = Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.ApproveVariables(Finn, "p1", App, store, Variables));

        Assert.Contains("changed since it was approved", e.Message);
        Assert.Empty(store.Get(Finn, "p1")!.Variables);
    }

    [Fact]
    public void Approving_variables_alone_refuses_a_name_refused_for_elevated_runs()
    {
        ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store, Variables);

        var e = Assert.Throws<InvalidOperationException>(() =>
            ApprovedBuilds.ApproveVariables(Finn, "p1", App, store, [new("VK_LAYER_PATH", @"C:\x")]));

        Assert.Contains("VK_LAYER_PATH", e.Message);
        Assert.Equal(Variables, store.Get(Finn, "p1")!.Variables);
    }

    [Fact]
    public void An_approval_refuses_a_name_refused_for_elevated_runs()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store, [new("VK_LAYER_PATH", @"C:\x")]));

        Assert.Contains("VK_LAYER_PATH", e.Message);
        Assert.Null(store.Get(Finn, "p1"));
    }

    [Fact]
    public void A_bundled_commit_with_a_build_folder_is_refused()
    {
        var source = MakeInstall();

        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(Finn, "p1", ApprovedBuilds.BundledCommit, source, App, store));
        Assert.Null(store.Get(Finn, "p1"));
    }

    private string MakeInstallWithManifest(string manifest, bool libmonado = true)
    {
        var source = MakeInstall();
        File.WriteAllText(Path.Combine(source, "openxr_monado.json"), manifest);
        if (libmonado)
        {
            File.WriteAllText(Path.Combine(source, "bin", "monado.dll"), "libmonado v1");
        }

        return source;
    }

    private static string Runtime(string fields) =>
        $$$"""{"file_format_version": "1.0.0", "runtime": {"name": "Monado", "library_path": "bin\\openxr_monado.dll"{{{fields}}}}}""";

    private static JsonElement RuntimeOf(string manifest)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        return doc.RootElement.GetProperty("runtime").Clone();
    }

    private static string? Field(string manifest, string name) =>
        RuntimeOf(manifest).TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string Json(string text) => JsonSerializer.Serialize(text);

    [Fact]
    public void An_absolute_libmonado_path_into_the_build_names_the_same_file_in_the_copy()
    {
        var source = MakeInstallWithManifest(Runtime($", \"MND_libmonado_path\": {Json(temp.File("user", "install", "bin", "monado.dll"))}"));

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.Equal(@"bin\monado.dll", Field(build.Manifest, "MND_libmonado_path"));
        Assert.Equal(@"bin\openxr_monado.dll", LibraryPathOf(build.Manifest));
        Assert.Equal("Monado (Mirador, approved build)", Field(build.Manifest, "name"));
        Assert.Null(ApprovedBuilds.Verify(build, App));
        Assert.True(ApprovedBuilds.NamesOwnDll(build));
    }

    [Theory]
    [InlineData("bin/monado.dll", @"bin\monado.dll")]
    [InlineData(@"bin\\monado.dll", @"bin\monado.dll")]
    [InlineData(@".\\lib\\..\\bin\\monado.dll", @"bin\monado.dll")]
    [InlineData("lib/monado.dll", @"lib\monado.dll")]
    [InlineData("bin/monado.dll::$DATA", @"bin\monado.dll")]
    public void A_relative_path_is_rewritten_relative_to_the_copys_manifest_with_backslashes(string given, string written)
    {
        var source = MakeInstallWithManifest(Runtime($", \"MND_libmonado_path\": \"{given}\""));
        Directory.CreateDirectory(Path.Combine(source, "lib"));
        File.WriteAllText(Path.Combine(source, "lib", "monado.dll"), "libmonado in lib");

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.Equal(written, Field(build.Manifest, "MND_libmonado_path"));
        Assert.True(File.Exists(Path.Combine(build.Folder, written)));
    }

    [Fact]
    public void A_bare_file_name_stays_only_as_the_copys_bin_file_of_that_name()
    {
        var source = MakeInstallWithManifest(Runtime(", \"MND_libmonado_path\": \"monado.dll\", \"MND_other_path\": \"other.dll\""));
        var log = new List<string>();

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store, log: log.Add);

        Assert.Equal(@"bin\monado.dll", Field(build.Manifest, "MND_libmonado_path"));
        Assert.False(RuntimeOf(build.Manifest).TryGetProperty("MND_other_path", out _));
        Assert.Contains(log, l => l.Contains("MND_other_path", StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_outside_the_build_is_left_out_and_libmonado_is_named_as_the_copys_own()
    {
        var elsewhere = temp.File("user", "elsewhere", "monado.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "someone else's");
        var source = MakeInstallWithManifest(Runtime(
            $", \"MND_libmonado_path\": {Json(elsewhere)}, \"MND_up_path\": \"..\\\\elsewhere\\\\monado.dll\", \"MND_gone_path\": \"bin\\\\gone.dll\", \"MND_number_path\": 3"));
        var log = new List<string>();

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store, log: log.Add);

        var runtime = RuntimeOf(build.Manifest);
        Assert.Equal(@"bin\monado.dll", Field(build.Manifest, "MND_libmonado_path"));
        Assert.False(runtime.TryGetProperty("MND_up_path", out _));
        Assert.False(runtime.TryGetProperty("MND_gone_path", out _));
        Assert.False(runtime.TryGetProperty("MND_number_path", out _));
        Assert.Equal(4, log.Count);
        Assert.Null(ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_path_outside_the_build_is_left_out_without_a_libmonado_in_the_copy()
    {
        var source = MakeInstallWithManifest(Runtime(", \"MND_libmonado_path\": \"C:\\\\Windows\\\\System32\\\\kernel32.dll\""), libmonado: false);

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.False(RuntimeOf(build.Manifest).TryGetProperty("MND_libmonado_path", out _));
    }

    [Fact]
    public void Monados_other_fields_are_carried_as_they_are_and_other_keys_are_not()
    {
        var source = MakeInstallWithManifest(Runtime(
            ", \"MND_text\": \"a\\\\b/c\", \"MND_number\": 1.5, \"MND_flag\": true, \"MND_object\": {\"x\": 1}, \"api_version\": \"1.0\", \"functions\": {}, \"XYZ_dll_path\": \"bin\\\\monado.dll\", \"mnd_lower\": 1"),
            libmonado: false);

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        var runtime = RuntimeOf(build.Manifest);
        Assert.Equal(@"a\b/c", runtime.GetProperty("MND_text").GetString());
        Assert.Equal(1.5, runtime.GetProperty("MND_number").GetDouble());
        Assert.True(runtime.GetProperty("MND_flag").GetBoolean());
        Assert.Equal(["name", "library_path", "MND_text", "MND_number", "MND_flag"], runtime.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void The_copys_manifest_keeps_its_own_name_and_dll_whatever_the_builds_says()
    {
        var source = MakeInstallWithManifest(
            $$$"""{"file_format_version": "1.0.0", "runtime": {"name": "Not Monado", "library_path": {{{Json(Path.Combine(temp.Path, "evil.dll"))}}}}}""");

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.Equal("Monado (Mirador, approved build)", Field(build.Manifest, "name"));
        Assert.Equal(@"bin\openxr_monado.dll", LibraryPathOf(build.Manifest));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"runtime": "x"}""")]
    [InlineData("[]")]
    public void A_build_manifest_without_monados_fields_still_names_the_copys_libmonado(string manifest)
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstallWithManifest(manifest), App, store);

        Assert.Equal(@"bin\monado.dll", Field(build.Manifest, "MND_libmonado_path"));
        Assert.Equal(@"bin\openxr_monado.dll", LibraryPathOf(build.Manifest));
        Assert.Null(ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_build_without_libmonado_or_a_readable_manifest_gets_the_plain_manifest()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstallWithManifest("not json", libmonado: false), App, store);

        Assert.Equal(["name", "library_path"], RuntimeOf(build.Manifest).EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_missing_build_manifest_writes_the_plain_manifest()
    {
        var copy = temp.File("copy");
        Directory.CreateDirectory(Path.Combine(copy, "bin"));

        ApprovedBuilds.WriteManifest(temp.File("user", "install"), copy);

        var manifest = Path.Combine(copy, "openxr_monado.json");
        Assert.Equal(["name", "library_path"], RuntimeOf(manifest).EnumerateObject().Select(p => p.Name));
        Assert.Equal(@"bin\openxr_monado.dll", LibraryPathOf(manifest));
    }

    [Fact]
    public void Libmonado_is_hashed_with_the_copy_and_a_rebuilt_one_is_not_the_approved_build()
    {
        var source = MakeInstallWithManifest(Runtime(", \"MND_libmonado_path\": \"bin\\\\monado.dll\""));
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.True(build.Files.ContainsKey(ApprovedBuilds.LibmonadoDll));
        Assert.True(ApprovedBuilds.Matches(build, source));

        File.WriteAllText(Path.Combine(source, "bin", "monado.dll"), "libmonado v2");
        Assert.False(ApprovedBuilds.Matches(build, source));

        File.Delete(Path.Combine(source, "bin", "monado.dll"));
        Assert.False(ApprovedBuilds.Matches(build, source));

        File.WriteAllText(Path.Combine(build.ServerDir, "monado.dll"), "changed in the copy");
        Assert.NotNull(ApprovedBuilds.Verify(build, App));
    }

    [Theory]
    [InlineData(@"bin\vulkan-1.dll")]
    [InlineData(@"bin\msvcp140.dll")]
    [InlineData(@"share\monado\data.json")]
    [InlineData("notes.txt")]
    public void A_file_added_to_the_build_after_the_approval_is_not_the_approved_build(string added)
    {
        var source = MakeInstall();
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        var path = Path.Combine(source, added);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "added");

        Assert.False(ApprovedBuilds.Matches(build, source));
    }

    [Fact]
    public void A_file_removed_from_the_build_or_changed_in_a_subfolder_is_not_the_approved_build()
    {
        var source = MakeInstall();
        Directory.CreateDirectory(Path.Combine(source, "share", "monado"));
        File.WriteAllText(Path.Combine(source, "share", "monado", "data.json"), "v1");
        File.WriteAllText(Path.Combine(source, "bin", "extra.dll"), "v1");
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);
        Assert.True(ApprovedBuilds.Matches(build, source));

        File.WriteAllText(Path.Combine(source, "share", "monado", "data.json"), "v2");
        Assert.False(ApprovedBuilds.Matches(build, source));

        File.WriteAllText(Path.Combine(source, "share", "monado", "data.json"), "v1");
        Assert.True(ApprovedBuilds.Matches(build, source));

        File.Delete(Path.Combine(source, "bin", "extra.dll"));
        Assert.False(ApprovedBuilds.Matches(build, source));
    }

    [Fact]
    public void The_files_the_approval_writes_itself_are_not_compared()
    {
        var source = MakeInstallWithManifest(Runtime(", \"MND_libmonado_path\": \"bin\\\\monado.dll\""));
        Directory.CreateDirectory(Path.Combine(source, "bin", "service"));
        File.WriteAllText(Path.Combine(source, "bin", "service", "monado-service.exe"), "the build's own");
        Directory.CreateDirectory(Path.Combine(App, "standin"));
        File.WriteAllText(Path.Combine(App, "standin", "mirador-standin.exe"), "mirador's stand-in");

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.NotEqual(File.ReadAllText(Path.Combine(source, "openxr_monado.json")), File.ReadAllText(build.Manifest));
        Assert.True(ApprovedBuilds.Matches(build, source));
    }

    [Fact]
    public void A_link_added_to_the_build_after_the_approval_is_not_the_approved_build()
    {
        var source = MakeInstall();
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);
        Directory.CreateDirectory(temp.File("elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(source, "bin", "plugins"), temp.File("elsewhere"));

        Assert.False(ApprovedBuilds.Matches(build, source));
    }

    [Fact]
    public void A_build_folder_that_is_gone_or_a_bundled_approval_is_never_matched()
    {
        var source = MakeInstall();
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);
        var bundled = ApprovedBuilds.Approve(Finn, "p2", ApprovedBuilds.BundledCommit, "", App, store);

        Assert.False(ApprovedBuilds.Matches(bundled, source));
        Assert.False(ApprovedBuilds.Matches(build, temp.File("user", "missing")));
    }

    [Fact]
    public void A_libmonado_added_to_the_build_after_the_approval_is_not_the_approved_build()
    {
        var source = MakeInstall();
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        File.WriteAllText(Path.Combine(source, "bin", "monado.dll"), "libmonado v1");

        Assert.False(ApprovedBuilds.Matches(build, source));
    }

    [Theory]
    [InlineData("0123abc", true)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("bundled", false)]
    [InlineData("folder", false)]
    [InlineData("0123ab", false)]
    [InlineData("0123ABC", false)]
    [InlineData("../x", false)]
    public void Only_hex_is_a_git_commit(string commit, bool isCommit) => Assert.Equal(isCommit, ApprovedBuilds.IsGitCommit(commit));
}

internal static class NativeLoad
{
    public const int ModNotFound = 126;
    public const uint SearchDllLoadDir = 0x100;
    public const uint SearchDefaultDirs = 0x1000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string fileName);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr module);
}
