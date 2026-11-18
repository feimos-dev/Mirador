using Mirador.Core.Profiles;

namespace Mirador.Core.Build;

/// <summary>A repository's branches couldn't be listed; the message says why, for the user.</summary>
public sealed class GitRemoteException(string message) : Exception(message);

/// <summary>
/// A repository's branches, from <c>git ls-remote --heads</c>, for the Profiles page's branch drop-down. The link is
/// checked as a branch profile's is (<see cref="BranchLink.UrlProblem"/>: an https:// repository, nothing git could
/// read as an option or a path) and passed as one argument, never through a shell. Git never asks for a password: a
/// repository that needs one fails at once.
/// </summary>
public static class GitRemote
{
    /// <summary>How long a listing may take before git is ended.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private const string Failed = "Couldn't list the branches of that link: ";

    /// <summary>The branches of the repository at <paramref name="url"/>, in git's order, with git from <c>PATH</c>.</summary>
    /// <exception cref="GitRemoteException">The link isn't one to list, git failed, or it took longer than <see cref="Limit"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; git has been ended.</exception>
    public static Task<IReadOnlyList<string>> ListBranchesAsync(string url, CancellationToken ct) =>
        ListBranchesAsync(url, "git", new CommandRunner(), Limit, ct);

    /// <inheritdoc cref="ListBranchesAsync(string, CancellationToken)"/>
    /// <param name="git">The git to run: Mirador's copy (<see cref="BuildTools.Git"/>) or one on <c>PATH</c>.</param>
    /// <param name="runner">Runs git; cancelling it ends git and what it started.</param>
    /// <param name="limit">How long git may take.</param>
    public static async Task<IReadOnlyList<string>> ListBranchesAsync(
        string url, string git, ICommandRunner runner, TimeSpan limit, CancellationToken ct)
    {
        if (BranchLink.UrlProblem(url) is { } problem)
        {
            throw new GitRemoteException(Failed + problem);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var lines = new List<string>();
        int code;
        try
        {
            code = await runner.RunAsync(
                new CommandSpec(git, ["ls-remote", "--heads", url], Environment: Quiet),
                line =>
                {
                    lock (lines)
                    {
                        lines.Add(line);
                    }
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitRemoteException($"{Failed}git didn't answer within {limit.TotalSeconds:0} s.");
        }
        catch (MiradorException e)
        {
            throw new GitRemoteException(Failed + e.Message);
        }

        string output;
        lock (lines)
        {
            output = string.Join('\n', lines);
        }

        if (code != 0)
        {
            var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new GitRemoteException(Failed + (last ?? $"git ended with exit code {code}."));
        }

        return ParseHeads(output);
    }

    /// <summary>The branch names in <c>git ls-remote</c> output (<c>&lt;sha&gt;\trefs/heads/&lt;name&gt;</c>), in order; every other line is left out.</summary>
    public static IReadOnlyList<string> ParseHeads(string output)
    {
        const string Heads = "refs/heads/";
        var names = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || !line[..tab].All(Uri.IsHexDigit))
            {
                continue;
            }

            var reference = line[(tab + 1)..];
            if (reference.StartsWith(Heads, StringComparison.Ordinal) && reference.Length > Heads.Length && !reference.EndsWith("^{}", StringComparison.Ordinal))
            {
                names.Add(reference[Heads.Length..]);
            }
        }

        return names;
    }

    /// <summary>Git and Git Credential Manager never ask: a repository that needs signing in fails instead of waiting.</summary>
    private static readonly IReadOnlyDictionary<string, string?> Quiet = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
    };
}
