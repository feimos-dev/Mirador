using System.Security.Principal;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

/// <summary>The helper's side of approved builds: as the runtime, and taken back (Mirador.md §4.11 item 51).</summary>
public sealed class HelperHandlerApprovalTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly SecurityIdentifier Finn = new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier Other = new("S-1-5-21-1-2-3-1002");

    private readonly TempDir temp = new();
    private readonly FakeRuntimeRegistry registry = new();
    private readonly FakeApprovedBuildStore store = new();
    private readonly FakeConsentValues consent = new();
    private readonly FakeMonadoInstaller monado = new();

    public void Dispose() => temp.Dispose();

    private string App => temp.File("app");

    private CallerInfo Caller(SecurityIdentifier sid) => new(sid, 1, 4242, Path.Combine(App, "Mirador.exe"));

    private HelperHandler Handler() =>
        new(new FakeServiceControl(), new FakeProcessControl(), registry, "1.0",
            consent: new ElevatedConsent(consent), installDir: App, monado: monado, approved: store);

    private ApprovedBuild Approve(SecurityIdentifier owner, string profileId)
    {
        var folder = temp.File("user", owner.Value, profileId);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "bin", "openxr_monado.dll"), "dll");
        File.WriteAllText(Path.Combine(folder, "bin", "monado-service.exe"), "service");
        return ApprovedBuilds.Approve(owner.Value, profileId, Commit, folder, App, store);
    }

    [Fact]
    public async Task The_callers_approvals_are_listed_and_one_is_taken_back_never_another_users()
    {
        var gone = Approve(Finn, "p1");
        Approve(Finn, "p2");
        Approve(Other, "p1");

        var listed = await Handler().HandleAsync(new HelperRequest(HelperCommand.ListApprovedBuilds), Caller(Finn));
        Assert.True(listed.Ok, listed.Error);
        Assert.Equal(["p1", "p2"], listed.Value!.Split(',').Order());

        var reply = await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn));

        Assert.True(reply.Ok, reply.Error);
        Assert.Null(store.Get(Finn.Value, "p1"));
        Assert.False(Directory.Exists(gone.Folder));
        Assert.NotNull(store.Get(Finn.Value, "p2"));
        Assert.NotNull(store.Get(Other.Value, "p1"));
        Assert.Equal("p2", (await Handler().HandleAsync(new HelperRequest(HelperCommand.ListApprovedBuilds), Caller(Finn))).Value);
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_hands_it_to_miradors_monado()
    {
        var build = Approve(Finn, "p1");
        registry.Active = build.Manifest;
        var miradors = temp.File("app", "monado", "monado-2026.11.10-1a2b3c4d5e", "openxr_monado.json");
        Directory.CreateDirectory(Path.GetDirectoryName(miradors)!);
        File.WriteAllText(miradors, "{}");
        monado.Current = miradors;

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Equal(miradors, registry.Active);
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_without_the_bundled_monado_gives_back_the_runtime_it_replaced()
    {
        consent.Set(Finn.Value);
        var steamVr = temp.File("SteamVR", "steamxr_win64.json");
        Directory.CreateDirectory(Path.GetDirectoryName(steamVr)!);
        File.WriteAllText(steamVr, "{}");
        registry.Active = steamVr;
        var build = Approve(Finn, "p1");
        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn))).Ok);
        Assert.Equal(build.Manifest, registry.Active);

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Equal(steamVr, registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_doesnt_give_back_a_runtime_that_is_gone()
    {
        consent.Set(Finn.Value);
        registry.Active = temp.File("uninstalled", "steamxr_win64.json");
        var build = Approve(Finn, "p1");
        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn))).Ok);

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Null(registry.Active);
        Assert.Null(registry.Recorded);
        Assert.False(Directory.Exists(build.Folder));
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_with_nothing_to_go_back_to_leaves_no_runtime()
    {
        var build = Approve(Finn, "p1");
        registry.Active = build.Manifest;

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Null(registry.Active);
    }

    [Fact]
    public async Task Taking_the_consent_back_keeps_the_approvals()
    {
        consent.Set(Finn.Value);
        Approve(Finn, "p1");
        Approve(Finn, "p2");

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.RevokeElevatedMonado), Caller(Finn))).Ok);

        Assert.Equal(["p1", "p2"], store.ProfilesOf(Finn.Value).Order());
    }

    [Fact]
    public async Task An_approved_build_becomes_the_runtime_without_the_consent_for_monado_as_administrator()
    {
        var build = Approve(Finn, "p1");
        registry.Active = @"C:\SteamVR\steamxr.json";

        var reply = await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn));

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(build.Manifest, registry.Active);
        Assert.Equal(@"C:\SteamVR\steamxr.json", registry.Recorded);
    }

    [Fact]
    public async Task Another_users_approval_and_a_changed_copy_are_refused_leaving_the_runtime()
    {
        var build = Approve(Finn, "p1");
        consent.Set(Other.Value);
        consent.Set(Finn.Value);
        registry.Active = @"C:\SteamVR\steamxr.json";

        Assert.False((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Other))).Ok);

        File.WriteAllText(Path.Combine(build.ServerDir, "monado-service.exe"), "changed after the approval");
        Assert.False((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn))).Ok);
        Assert.Equal(@"C:\SteamVR\steamxr.json", registry.Active);
    }

    // The variables an approval covers, checked by the helper at each elevated Start (ElevatedRequestCheck.ApprovedVariables).

    private static ElevatedStart StartWith(string profileId, IReadOnlyList<KeyValuePair<string, string>>? variables, string build = "") =>
        new(3000, 100, 6, false, "", build, ProfileId: profileId, Variables: variables);

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Approved = [new("PIMAX_P3C_LOG", "debug"), new("XRT_LOG", "info")];

    private static string? Check(ElevatedStart start, IApprovedBuildStore store, out IReadOnlyList<KeyValuePair<string, string>> variables) =>
        ElevatedMonado.ElevatedRequestCheck.ApprovedVariables(start, Finn.Value, store, out variables);

    [Fact]
    public void The_helper_refuses_variables_that_differ_from_the_approval()
    {
        ApprovedBuilds.Approve(Finn.Value, "p1", ApprovedBuilds.BundledCommit, "", App, store, Approved);
        const string Changed = "This profile's variables changed since they were approved. Approve them again on the Profiles page.";

        Assert.Equal(Changed, Check(StartWith("p1", [new("PIMAX_P3C_LOG", "info"), new("XRT_LOG", "info")]), store, out _));
        Assert.Equal(Changed, Check(StartWith("p1", [new("XRT_LOG", "info"), new("PIMAX_P3C_LOG", "debug")]), store, out _));
        Assert.Equal(Changed, Check(StartWith("p1", [.. Approved, new("XRT_EXTRA", "1")]), store, out _));
        Assert.Equal(Changed, Check(StartWith("p1", [Approved[0]]), store, out _));

        // The same variables, a name spelt in another case as Windows allows: the start goes ahead with them.
        Assert.Null(Check(StartWith("p1", [new("pimax_p3c_log", "debug"), new("XRT_LOG", "info")]), store, out var variables));
        Assert.Equal(2, variables.Count);
    }

    [Fact]
    public void The_helper_refuses_a_refused_name_even_when_approved()
    {
        IReadOnlyList<KeyValuePair<string, string>> withPath = [new("PIMAX_P3C_LOG", "debug"), new("Path", @"C:\Users\finn\bin")];
        store.Set(new ApprovedBuild(Finn.Value, "p1", ApprovedBuilds.BundledCommit, "", new Dictionary<string, string>(), withPath));

        var refusal = Check(StartWith("p1", withPath), store, out var variables);

        Assert.NotNull(refusal);
        Assert.Contains("Path", refusal);
        Assert.Empty(variables);
    }

    [Fact]
    public void Variables_without_the_callers_approval_are_refused()
    {
        ApprovedBuilds.Approve(Other.Value, "p1", ApprovedBuilds.BundledCommit, "", App, store, Approved);

        Assert.NotNull(Check(StartWith("p1", Approved), store, out _));
        Assert.NotNull(Check(StartWith("p2", Approved), store, out _));
        Assert.NotNull(Check(StartWith("../p1", Approved), store, out _));
    }

    [Fact]
    public void A_build_start_needs_the_builds_own_approval_of_the_variables()
    {
        ApprovedBuilds.Approve(Finn.Value, "p1", ApprovedBuilds.BundledCommit, "", App, store, Approved);

        // The record is for the bundled Monado: a build's start isn't covered by it.
        Assert.NotNull(Check(StartWith("p1", Approved, build: "p1"), store, out _));

        var folder = temp.File("user", "p2");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "bin", "openxr_monado.dll"), "dll");
        File.WriteAllText(Path.Combine(folder, "bin", "monado-service.exe"), "service");
        ApprovedBuilds.Approve(Finn.Value, "p2", Commit, folder, App, store, Approved);

        Assert.Null(Check(StartWith("p2", Approved, build: "p2"), store, out _));
        Assert.NotNull(Check(StartWith("p2", Approved), store, out _)); // the bundled Monado isn't the approved build
        Assert.NotNull(Check(StartWith("p2", Approved, build: "p1"), store, out _));
    }

    [Fact]
    public void A_start_without_variables_needs_no_approval_of_them()
    {
        Assert.Null(Check(StartWith("p1", null), store, out var none));
        Assert.Empty(none);
        Assert.Null(Check(StartWith("", []), store, out var empty));
        Assert.Empty(empty);
    }
}
