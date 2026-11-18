using Mirador.Core.Profiles;

namespace Mirador.Core.Build;

/// <summary>
/// The Profiles page's "Branch" drop-down: a repository's branches, listed once per link (<see cref="GitRemote"/>), and
/// which one to pick. Leaving the link box again on the same link lists nothing and changes nothing; only another link
/// (or <see cref="Forget"/>) lists again.
/// </summary>
/// <param name="git">The git to run (<see cref="BuildTools.Git"/>).</param>
/// <param name="runner">Runs git; cancelling ends it.</param>
public sealed class BranchOffer(string git, ICommandRunner runner)
{
    private string? listedUrl;

    /// <summary>
    /// The branches of the repository at <paramref name="url"/>; null when they are already listed for that very link,
    /// so the drop-down stays as it is. A listing that fails or is cancelled is tried again next time.
    /// </summary>
    /// <exception cref="GitRemoteException">The branches couldn't be listed (<see cref="GitRemote.ListBranchesAsync(string, string, ICommandRunner, TimeSpan, CancellationToken)"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<IReadOnlyList<string>?> ListAsync(string url, CancellationToken ct)
    {
        if (url == listedUrl)
        {
            return null;
        }

        var branches = await GitRemote.ListBranchesAsync(url, git, runner, GitRemote.Limit, ct).ConfigureAwait(false);
        listedUrl = url;
        return branches;
    }

    /// <summary>The link changed, or another profile is shown: the next listing runs whatever the link.</summary>
    public void Forget() => listedUrl = null;

    /// <summary>
    /// The drop-down's items and pick: the profile's branch when listed, else <c>main</c>, else the first. With
    /// <paramref name="keepCurrent"/> (a saved profile just opened), a branch the repository no longer lists stays,
    /// first in the list, and picked.
    /// </summary>
    public static (IReadOnlyList<string> Items, string Pick) Offer(IReadOnlyList<string> branches, string current, bool keepCurrent)
    {
        IReadOnlyList<string> items = keepCurrent && current.Length > 0 && !branches.Contains(current) ? [current, .. branches] : branches;
        var pick = items.Contains(current) ? current
            : branches.Contains(BuildSource.UpstreamRef) ? BuildSource.UpstreamRef
            : branches.FirstOrDefault() ?? current;
        return (items, pick);
    }
}
