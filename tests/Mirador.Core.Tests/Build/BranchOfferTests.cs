using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

public class BranchOfferTests
{
    private const string Url = "https://gitlab.freedesktop.org/fzeruhn/monado.git";
    private const string Other = "https://github.com/fzeruhn/monado.git";

    private static FakeCommandRunner Runner(params string[] heads) => new()
    {
        Answer = _ => ([.. heads.Select(h => "1111111111111111111111111111111111111111\trefs/heads/" + h)], 0),
    };

    [Fact]
    public async Task An_unchanged_link_is_not_listed_again()
    {
        var runner = Runner("main", "pimax-p3c-windows");
        var offer = new BranchOffer(() => "git", runner);

        Assert.Equal(["main", "pimax-p3c-windows"], await offer.ListAsync(Url, CancellationToken.None));
        Assert.Null(await offer.ListAsync(Url, CancellationToken.None)); // tabbing through the link: nothing to do

        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task A_changed_or_forgotten_link_is_listed_again()
    {
        var runner = Runner("main");
        var offer = new BranchOffer(() => "git", runner);
        await offer.ListAsync(Url, CancellationToken.None);

        Assert.NotNull(await offer.ListAsync(Other, CancellationToken.None));
        offer.Forget();
        Assert.NotNull(await offer.ListAsync(Other, CancellationToken.None));

        Assert.Equal(3, runner.Commands.Count);
    }

    [Fact]
    public async Task A_failed_listing_is_tried_again()
    {
        var runner = new FakeCommandRunner { Answer = _ => (["fatal: unable to access"], 128) };
        var offer = new BranchOffer(() => "git", runner);

        await Assert.ThrowsAsync<GitRemoteException>(() => offer.ListAsync(Url, CancellationToken.None));
        await Assert.ThrowsAsync<GitRemoteException>(() => offer.ListAsync(Url, CancellationToken.None));

        Assert.Equal(2, runner.Commands.Count);
    }

    [Theory]
    [InlineData("pimax-p3c-windows", false, "pimax-p3c-windows")] // the profile's branch, listed
    [InlineData("gone", false, "main")] // not listed: main
    [InlineData("gone", true, "gone")] // a saved profile's: kept, listed first
    [InlineData("", true, "main")]
    public void The_pick_is_the_profiles_branch_else_main_else_the_first(string current, bool keepCurrent, string pick)
    {
        var (items, picked) = BranchOffer.Offer(["main", "pimax-p3c-windows"], current, keepCurrent);

        Assert.Equal(pick, picked);
        Assert.Contains(picked, items);
        Assert.Equal(keepCurrent && current == "gone" ? ["gone", "main", "pimax-p3c-windows"] : ["main", "pimax-p3c-windows"], items);
    }

    [Fact]
    public void Without_main_the_first_branch_is_picked() =>
        Assert.Equal("dev", BranchOffer.Offer(["dev", "release"], "gone", keepCurrent: false).Pick);

    [Fact]
    public async Task Each_listing_runs_the_git_mirador_finds_then()
    {
        var git = "git";
        var runner = Runner("main");
        var offer = new BranchOffer(() => git, runner);

        await offer.ListAsync(Url, CancellationToken.None);
        git = @"C:\Users\you\AppData\Local\Mirador\tools\git\cmd\git.exe"; // a build installed Mirador's git meanwhile
        await offer.ListAsync(Other, CancellationToken.None);

        Assert.Equal(["git", git], runner.Commands.Select(c => c.FileName));
    }
}
