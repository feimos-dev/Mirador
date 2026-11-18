using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

public class GitRemoteTests
{
    private const string Url = "https://gitlab.freedesktop.org/feimos/monado.git";

    [Fact]
    public void Parse_heads_lists_branch_names_in_order()
    {
        const string output = "1111111111111111111111111111111111111111\trefs/heads/main\n"
            + "2222222222222222222222222222222222222222\trefs/heads/pimax-p3c-windows\r\n"
            + "3333333333333333333333333333333333333333\trefs/heads/a/b\n";

        Assert.Equal(["main", "pimax-p3c-windows", "a/b"], GitRemote.ParseHeads(output));
    }

    [Fact]
    public void Parse_heads_ignores_other_refs()
    {
        const string output = "1111111111111111111111111111111111111111\tHEAD\n"
            + "2222222222222222222222222222222222222222\trefs/heads/main\n"
            + "3333333333333333333333333333333333333333\trefs/tags/v1.0\n"
            + "4444444444444444444444444444444444444444\trefs/tags/v1.0^{}\n"
            + "5555555555555555555555555555555555555555\trefs/merge-requests/3107/head\n"
            + "warning: redirecting to https://gitlab.freedesktop.org/feimos/monado.git/\n"
            + "\n";

        Assert.Equal(["main"], GitRemote.ParseHeads(output));
    }

    [Fact]
    public async Task Listing_runs_git_ls_remote_with_the_link_as_one_argument_and_no_prompt()
    {
        var runner = new FakeCommandRunner { Answer = _ => (["1111111111111111111111111111111111111111\trefs/heads/main"], 0) };

        var branches = await GitRemote.ListBranchesAsync(Url, @"C:\Tools\git\cmd\git.exe", runner, GitRemote.Limit, CancellationToken.None);

        Assert.Equal(["main"], branches);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(@"C:\Tools\git\cmd\git.exe", command.FileName);
        Assert.Equal(["ls-remote", "--heads", Url], command.Arguments);
        Assert.Equal("0", command.Environment!["GIT_TERMINAL_PROMPT"]);
    }

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("-c")]
    [InlineData("C:\\Users\\someone\\monado")]
    [InlineData("ext::sh -c touch% /tmp/x")]
    public async Task A_link_git_could_read_as_an_option_or_a_path_is_refused_before_git_runs(string url)
    {
        var runner = new FakeCommandRunner();

        var e = await Assert.ThrowsAsync<GitRemoteException>(() => GitRemote.ListBranchesAsync(url, "git", runner, GitRemote.Limit, CancellationToken.None));

        Assert.StartsWith("Couldn't list the branches of that link: ", e.Message);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_failure_says_what_git_said()
    {
        var runner = new FakeCommandRunner { Answer = _ => (["remote: The project you were looking for could not be found.", "fatal: repository not found"], 128) };

        var e = await Assert.ThrowsAsync<GitRemoteException>(() => GitRemote.ListBranchesAsync(Url, "git", runner, GitRemote.Limit, CancellationToken.None));

        Assert.Equal("Couldn't list the branches of that link: fatal: repository not found", e.Message);
    }

    [Fact]
    public async Task A_listing_that_takes_too_long_gives_up_and_ends_git()
    {
        var ended = false;
        var runner = new FakeCommandRunner
        {
            Effect = async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    ended = ct.IsCancellationRequested;
                }
            },
        };

        var e = await Assert.ThrowsAsync<GitRemoteException>(() => GitRemote.ListBranchesAsync(Url, "git", runner, TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith("Couldn't list the branches of that link: git didn't answer within", e.Message);
        Assert.True(ended);
    }

    [Fact]
    public async Task A_cancelled_listing_is_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new FakeCommandRunner { Effect = (_, ct) => Task.Delay(Timeout.Infinite, ct) };

        var listing = GitRemote.ListBranchesAsync(Url, "git", runner, GitRemote.Limit, cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Without_git_the_message_says_what_to_do()
    {
        var runner = new FakeCommandRunner { Effect = (_, _) => throw new MiradorException("git couldn't be started: The system cannot find the file specified.") };

        var e = await Assert.ThrowsAsync<GitRemoteException>(() => GitRemote.ListBranchesAsync(Url, "git", runner, GitRemote.Limit, CancellationToken.None));

        Assert.Equal("Mirador gets git with its first build. Build once, or paste a link to the branch.", e.Message);
    }
}
