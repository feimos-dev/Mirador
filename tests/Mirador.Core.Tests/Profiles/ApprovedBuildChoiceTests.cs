using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

public class ApprovedBuildChoiceTests
{
    private static Profile Branch(bool runAsAdmin) =>
        new("p1", "Fork", null, BuildSource.UpstreamMain, [], new Dictionary<string, string>(), RunAsAdministrator: runAsAdmin);

    [Fact]
    public void A_branch_runs_as_you_unless_run_as_administrator_is_on_and_its_build_approved()
    {
        Assert.Equal(Mirador.Core.Elevation.ElevatedChoice.BuildFolder, Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Branch(false), null, true, approvedBuild: true));
        Assert.Equal(Mirador.Core.Elevation.ElevatedChoice.NotApproved, Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Branch(true), null, true, approvedBuild: false));
        Assert.Null(Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Branch(true), null, true, approvedBuild: true, variablesApproved: true));
    }

    [Fact]
    public void Miradors_monado_may_run_as_administrator_without_an_approval()
    {
        var profile = new Profile("p", "P", null, BuildSource.Mirador, [], new Dictionary<string, string>());
        Assert.Null(Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(profile, null, true));
    }

    [Fact]
    public void The_approval_step_reads_its_three_values()
    {
        var args = AppArgs.Parse(["--approve-build", "p1", "abc1234", @"C:\Users\someone\AppData\Local\Mirador\Builds\p1\install"]);
        Assert.Equal(("p1", "abc1234", @"C:\Users\someone\AppData\Local\Mirador\Builds\p1\install"), (args.ApproveBuild!.Value.ProfileId, args.ApproveBuild.Value.Commit, args.ApproveBuild.Value.Source));
        Assert.Null(AppArgs.Parse(["--approve-build", "p1"]).ApproveBuild);
    }

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Approved = [new("PIMAX_P3C_LOG", "info"), new("XRT_LOG", "debug")];

    [Fact]
    public void Variables_are_approved_only_exactly_as_approved()
    {
        Assert.True(Mirador.Core.Elevation.ElevatedChoice.SameVariables([new("PIMAX_P3C_LOG", "info"), new("XRT_LOG", "debug")], Approved));
        Assert.True(Mirador.Core.Elevation.ElevatedChoice.SameVariables([new("pimax_p3c_log", "info"), new("XRT_LOG", "debug")], Approved));
        Assert.False(Mirador.Core.Elevation.ElevatedChoice.SameVariables([new("PIMAX_P3C_LOG", "debug"), new("XRT_LOG", "debug")], Approved));
        Assert.False(Mirador.Core.Elevation.ElevatedChoice.SameVariables([new("XRT_LOG", "debug"), new("PIMAX_P3C_LOG", "info")], Approved));
        Assert.False(Mirador.Core.Elevation.ElevatedChoice.SameVariables([new("PIMAX_P3C_LOG", "info")], Approved));
        Assert.False(Mirador.Core.Elevation.ElevatedChoice.SameVariables([], Approved));
        Assert.True(Mirador.Core.Elevation.ElevatedChoice.SameVariables([], []));
    }

    [Fact]
    public void Variables_too_long_for_the_helper_are_not_offered_for_approval()
    {
        var profile = new Profile("p1", "P", null, BuildSource.Mirador, [new EnvVar("XRT_LOG", new string('x', 5000))], new Dictionary<string, string>());

        Assert.Equal(
            "These variables are too long to run as administrator. Shorten them, or run this profile as you.",
            Mirador.Core.Elevation.ElevatedChoice.ApprovalProblem(profile));
        Assert.Null(Mirador.Core.Elevation.ElevatedChoice.ApprovalProblem(profile with { ExtraEnvironment = [new EnvVar("XRT_LOG", new string('x', 3000))] }));
    }

    [Fact]
    public void A_refused_variable_is_not_offered_for_approval()
    {
        var profile = new Profile("p1", "P", null, BuildSource.Mirador, [new EnvVar("XRT_LOG", "debug"), new EnvVar("STEAMVR_PATH", @"C:\x")], new Dictionary<string, string>());

        Assert.Equal(
            "STEAMVR_PATH can't be set for Monado as administrator. Remove it, or run this profile as you.",
            Mirador.Core.Elevation.ElevatedChoice.ApprovalProblem(profile));
    }

    private static Profile Saved(bool runAsAdmin, params EnvVar[] extra) =>
        new("p1", "Fork", null, BuildSource.UpstreamMain, extra, new Dictionary<string, string>(), RunAsAdministrator: runAsAdmin);

    [Fact]
    public void Save_approves_only_when_the_switch_turns_on_or_the_variables_change()
    {
        var log = new EnvVar("PIMAX_P3C_LOG", "info");

        Assert.True(Mirador.Core.Elevation.ElevatedChoice.ApprovesOnSave(Saved(false), Saved(true)));
        Assert.False(Mirador.Core.Elevation.ElevatedChoice.KeepsSwitchWhenDeclined(Saved(false)));
        Assert.True(Mirador.Core.Elevation.ElevatedChoice.ApprovesOnSave(null, Saved(true)));

        Assert.False(Mirador.Core.Elevation.ElevatedChoice.ApprovesOnSave(Saved(true, log), Saved(true, log) with { Name = "Renamed" }));
        Assert.True(Mirador.Core.Elevation.ElevatedChoice.ApprovesOnSave(Saved(true, log), Saved(true, log with { Value = "debug" })));
        Assert.True(Mirador.Core.Elevation.ElevatedChoice.ApprovesOnSave(Saved(true, log), Saved(true)));
        Assert.True(Mirador.Core.Elevation.ElevatedChoice.KeepsSwitchWhenDeclined(Saved(true, log)));

        Assert.False(Mirador.Core.Elevation.ElevatedChoice.ApprovesOnSave(Saved(true), Saved(false)));
        Assert.False(Mirador.Core.Elevation.ElevatedChoice.ApprovesOnSave(Saved(false), Saved(false, log)));
    }
}
