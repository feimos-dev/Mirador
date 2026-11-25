using Mirador.Core.Profiles;

namespace Mirador.Core.Build;

public sealed class BranchOffer(Func<string> git, ICommandRunner runner)
{
    private string? listedUrl;

    public async Task<IReadOnlyList<string>?> ListAsync(string url, CancellationToken ct)
    {
        if (url == listedUrl)
        {
            return null;
        }

        var branches = await GitRemote.ListBranchesAsync(url, git(), runner, GitRemote.Limit, ct).ConfigureAwait(false);
        listedUrl = url;
        return branches;
    }

    public void Forget() => listedUrl = null;

    public static (IReadOnlyList<string> Items, string Pick) Offer(IReadOnlyList<string> branches, string current, bool keepCurrent)
    {
        IReadOnlyList<string> items = keepCurrent && current.Length > 0 && !branches.Contains(current) ? [current, .. branches] : branches;
        var pick = items.Contains(current) ? current
            : branches.Contains(BuildSource.UpstreamRef) ? BuildSource.UpstreamRef
            : branches.FirstOrDefault() ?? current;
        return (items, pick);
    }
}
