using Mirador.Core.Profiles;

namespace Mirador.Core.Build;

public sealed class GitRemoteException(string message) : Exception(message);

public static class GitRemote
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private const string Failed = "Couldn't list the branches of that link: ";

    public const string NoGit = "Mirador gets git with its first build. Build once, or paste a link to the branch.";

    public static Task<IReadOnlyList<string>> ListBranchesAsync(string url, CancellationToken ct) =>
        ListBranchesAsync(url, "git", new CommandRunner(), Limit, ct);

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
        catch (MiradorException)
        {
            throw new GitRemoteException(NoGit);
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

    private static readonly IReadOnlyDictionary<string, string?> Quiet = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
    };
}
