using Mirador.Core.Elevation;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Elevation;

public class ElevatedChoiceTests
{
    private static Profile Custom(BuildSource? monado = null, params EnvVar[] extra) =>
        new("p", "Custom", null, monado ?? BuildSource.Mirador, extra, new Dictionary<string, string>());

    [Fact]
    public void Installed_copy_with_a_plain_bundled_profile_may_run_elevated() =>
        Assert.Null(ElevatedChoice.RunsAsYouReason(Custom(), null, installedCopy: true));

    [Fact]
    public void Another_copy_runs_as_you_whatever_the_profile()
    {
        Assert.Equal(ElevatedChoice.NotInstalledCopy, ElevatedChoice.RunsAsYouReason(Custom(), null, installedCopy: false));
        Assert.Equal("Runs as you: this copy of Mirador isn't the installed one.", ElevatedChoice.NotInstalledCopy);
    }

    [Fact]
    public void Build_folder_and_extra_variables_run_as_you()
    {
        Assert.Equal(
            ElevatedChoice.BuildFolder,
            ElevatedChoice.RunsAsYouReason(Custom(new BuildSource(SourceKind.Folder, @"C:\monado\build")), null, installedCopy: true));
        Assert.Equal(
            ElevatedChoice.ExtraVariables,
            ElevatedChoice.RunsAsYouReason(Custom(null, new EnvVar("XRT_LOG", "debug")), null, installedCopy: true));
    }

    [Fact]
    public void The_consent_is_kept_only_while_a_profile_runs_as_administrator()
    {
        Assert.False(ElevatedChoice.ConsentNeeded(new ProfileSet([Custom()], "p")));
        Assert.True(ElevatedChoice.ConsentNeeded(new ProfileSet([Custom(), Custom() with { Id = "q", RunAsAdministrator = true }], "p")));
    }

    private static readonly EnvVar Log = new("PIMAX_P3C_LOG", "info");

    [Fact]
    public void Approved_variables_run_elevated() =>
        Assert.Null(ElevatedChoice.RunsAsYouReason(Custom(null, Log) with { RunAsAdministrator = true }, null, installedCopy: true, variablesApproved: true));

    [Fact]
    public void Unapproved_variables_run_as_you()
    {
        Assert.Equal(ElevatedChoice.ExtraVariables, ElevatedChoice.RunsAsYouReason(Custom(null, Log), null, installedCopy: true, variablesApproved: false));

        // An approved branch whose variables changed since: the approval covered both.
        var branch = Custom(BuildSource.UpstreamMain, Log) with { RunAsAdministrator = true };
        Assert.Equal(ElevatedChoice.ExtraVariables, ElevatedChoice.RunsAsYouReason(branch, null, installedCopy: true, approvedBuild: true, variablesApproved: false));
        Assert.Null(ElevatedChoice.RunsAsYouReason(branch, null, installedCopy: true, approvedBuild: true, variablesApproved: true));

        // And one whose approved variables were all removed.
        Assert.Equal(
            ElevatedChoice.ExtraVariables,
            ElevatedChoice.RunsAsYouReason(Custom(BuildSource.UpstreamMain) with { RunAsAdministrator = true }, null, true, approvedBuild: true, variablesApproved: false));
    }

    [Theory]
    [InlineData("STEAMVR_LH_ENABLE")]
    [InlineData("XDG_RUNTIME_DIR")]
    [InlineData("SystemRoot")]
    [InlineData("PIMAX_\uFF29")]
    public void A_refused_variable_runs_as_you_and_names_it(string name)
    {
        var reason = ElevatedChoice.RunsAsYouReason(Custom(null, Log, new EnvVar(name, "1")), null, installedCopy: true, variablesApproved: true);

        Assert.Equal(ElevatedChoice.RefusedVariable(name), reason);
        Assert.Equal($"Runs as you: {name} can't be set for Monado as administrator.", reason);
    }

    [Fact]
    public void A_bundled_profile_without_variables_needs_no_approval() =>
        Assert.Null(ElevatedChoice.RunsAsYouReason(Custom(), null, installedCopy: true, variablesApproved: false));
}
