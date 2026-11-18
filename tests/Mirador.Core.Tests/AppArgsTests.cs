namespace Mirador.Core.Tests;

public class AppArgsTests
{
    [Fact]
    public void No_arguments()
    {
        Assert.Equal(new AppArgs(false, false), AppArgs.Parse([]));
    }

    [Fact]
    public void Start_and_background()
    {
        Assert.Equal(new AppArgs(true, true), AppArgs.Parse(["--background", "--start"]));
    }

    [Fact]
    public void The_old_set_runtime_step_is_gone_and_its_manifest_is_ignored()
    {
        Assert.Equal(new AppArgs(false, false), AppArgs.Parse(["--set-runtime", @"C:\Users\Zoë\My Builds\monado\build\openxr_monado-dev.json"]));
    }

    [Fact]
    public void Parses_sign_in_and_remove_sign_in()
    {
        Assert.True(AppArgs.Parse(["--sign-in"]).SignIn);
        Assert.False(AppArgs.Parse(["--sign-in"]).RemoveSignIn);
        Assert.True(AppArgs.Parse(["--Remove-Sign-In"]).RemoveSignIn);
        Assert.False(AppArgs.Parse(["--remove-sign-in"]).SignIn);
        Assert.False(AppArgs.Parse(["--start"]).SignIn);
    }

    [Fact]
    public void Case_doesnt_matter_and_unknown_arguments_are_ignored()
    {
        Assert.Equal(new AppArgs(true, true), AppArgs.Parse(["--START", "-Embedding", "--Background", "x"]));
    }

    [Fact]
    public void Parses_allow_elevated_monado()
    {
        Assert.True(AppArgs.Parse(["--allow-elevated-monado"]).AllowElevatedMonado);
        Assert.True(AppArgs.Parse(["--Allow-Elevated-Monado"]).AllowElevatedMonado);
        Assert.False(AppArgs.Parse(["--start"]).AllowElevatedMonado);
        Assert.False(AppArgs.Parse([]).AllowElevatedMonado);
    }

    [Fact]
    public void Approve_build_parses_variables()
    {
        var args = AppArgs.Parse(["--approve-build", "p1", "abc1234", @"C:\Builds\p1\install", "PIMAX_P3C_LOG=info", "XRT_MODE=a=b", "EMPTY=", "--start"]);

        Assert.True(args.Start);
        var (profileId, commit, source, variables) = args.ApproveBuild!.Value;
        Assert.Equal(("p1", "abc1234", @"C:\Builds\p1\install"), (profileId, commit, source));
        Assert.Equal(
            [new("PIMAX_P3C_LOG", "info"), new("XRT_MODE", "a=b"), new KeyValuePair<string, string>("EMPTY", "")],
            variables);
    }

    [Fact]
    public void Approve_build_with_bundled_source_needs_no_folder()
    {
        var args = AppArgs.Parse(["--approve-build", "p1", "bundled", "bundled", "XRT_LOG=debug"]);

        var (profileId, commit, source, variables) = args.ApproveBuild!.Value;
        Assert.Equal(("p1", "bundled", "bundled"), (profileId, commit, source));
        Assert.Equal([new KeyValuePair<string, string>("XRT_LOG", "debug")], variables);
    }

    [Fact]
    public void Approve_build_without_variables_has_none()
    {
        var (_, _, _, variables) = AppArgs.Parse(["--approve-build", "p1", "abc1234", @"C:\b"]).ApproveBuild!.Value;
        Assert.Empty(variables);
        Assert.Null(AppArgs.Parse(["--approve-build", "p1", "abc1234"]).ApproveBuild);
    }

    [Fact]
    public void Approve_build_can_come_with_the_consent_step()
    {
        var args = AppArgs.Parse(["--allow-elevated-monado", "--approve-build", "p1", "bundled", "bundled", "XRT_LOG=debug"]);

        Assert.True(args.AllowElevatedMonado);
        Assert.NotNull(args.ApproveBuild);
    }

    [Fact]
    public void A_bundled_approval_is_only_of_miradors_own_monado()
    {
        Assert.Equal(("bundled", ""), AppArgs.ApprovalTarget("bundled", "bundled"));
        Assert.Equal(("0123abc", @"C:\x"), AppArgs.ApprovalTarget("0123abc", @"C:\x"));

        // A build folder whose build-info.json says "bundled", or a "bundled" folder with a real commit: refused.
        var (_, commit, source, _) = AppArgs.Parse(["--approve-build", "p1", "bundled", @"C:\x"]).ApproveBuild!.Value;
        Assert.Throws<InvalidOperationException>(() => AppArgs.ApprovalTarget(commit, source));
        Assert.Throws<InvalidOperationException>(() => AppArgs.ApprovalTarget("0123abc", "bundled"));
    }
}
