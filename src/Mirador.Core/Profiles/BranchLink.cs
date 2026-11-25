using System.Text.RegularExpressions;

namespace Mirador.Core.Profiles;

public static partial class BranchLink
{
    public static (string Url, string Ref) Parse(string link)
    {
        var text = (link ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new MiradorException("Paste the https:// link of a repository, a branch or a merge request on GitLab or GitHub.");
        }

        var path = uri.AbsolutePath.Trim('/');
        string repoPath, gitRef;
        if (MergeRequest().Match(path) is { Success: true } mr)
        {
            (repoPath, gitRef) = (mr.Groups[1].Value, $"refs/merge-requests/{mr.Groups[2].Value}/head");
        }
        else if (PullRequest().Match(path) is { Success: true } pr)
        {
            (repoPath, gitRef) = (pr.Groups[1].Value, $"refs/pull/{pr.Groups[2].Value}/head");
        }
        else if (Tree().Match(path) is { Success: true } tree)
        {
            (repoPath, gitRef) = (tree.Groups[1].Value, Uri.UnescapeDataString(tree.Groups[2].Value));
        }
        else if (!path.Contains("/-/", StringComparison.Ordinal) && path.Count(c => c == '/') >= 1
            && !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && path.Count(c => c == '/') != 1))
        {
            (repoPath, gitRef) = (path, BuildSource.UpstreamRef);
        }
        else
        {
            throw new MiradorException("That link is no repository, branch or merge request page. Paste the link of the branch's page.");
        }

        if (repoPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repoPath = repoPath[..^4];
        }

        var url = $"https://{uri.Authority}/{repoPath}.git";
        var problem = UrlProblem(url) ?? RefProblem(gitRef);
        return problem is null ? (url, gitRef) : throw new MiradorException(problem);
    }

    public static bool NamesItsBranch(string? link)
    {
        if (!Uri.TryCreate((link ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        return MergeRequest().IsMatch(path) || PullRequest().IsMatch(path) || Commit().IsMatch(path) || Tree().IsMatch(path);
    }

    public static string? UrlProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "The branch profile needs a repository link.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || url.Any(char.IsWhiteSpace) || url.StartsWith('-'))
        {
            return $"\"{url}\" isn't an https:// repository link. Mirador builds only from repositories on the web, such as GitLab or GitHub.";
        }

        return null;
    }

    public static string? RefProblem(string? gitRef)
    {
        if (string.IsNullOrWhiteSpace(gitRef))
        {
            return "The branch profile needs a branch.";
        }

        if (gitRef.StartsWith('-') || gitRef.StartsWith('/') || gitRef.EndsWith('/') || gitRef.EndsWith('.')
            || gitRef.EndsWith(".lock", StringComparison.Ordinal) || gitRef.Contains("..", StringComparison.Ordinal)
            || gitRef.Contains("//", StringComparison.Ordinal) || gitRef.Contains("@{", StringComparison.Ordinal)
            || gitRef.Contains("/.", StringComparison.Ordinal) || gitRef.StartsWith('.') || !RefCharacters().IsMatch(gitRef))
        {
            return $"\"{gitRef}\" isn't a branch name git accepts.";
        }

        return null;
    }

    public static string? OptionsProblem(IEnumerable<string>? options)
    {
        foreach (var option in options ?? [])
        {
            if (!CMakeDefine().IsMatch(option))
            {
                return $"\"{option}\" isn't a CMake setting of the form -DNAME=value.";
            }
        }

        return null;
    }

    public static IReadOnlyList<string> SplitOptions(string text) =>
        (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? Problem(BuildSource source) =>
        UrlProblem(source.Url) ?? RefProblem(source.Ref) ?? OptionsProblem(source.Options);

    [GeneratedRegex(@"^(.+?)/-/merge_requests/(\d+)(?:/.*)?$")]
    private static partial Regex MergeRequest();

    [GeneratedRegex(@"^([^/]+/[^/]+)/pull/(\d+)(?:/.*)?$")]
    private static partial Regex PullRequest();

    [GeneratedRegex(@"^(.+?)(?:/-)?/tree/(.+)$")]
    private static partial Regex Tree();

    [GeneratedRegex(@"^(.+?)(?:/-)?/commit/[0-9A-Fa-f]{7,40}(?:/.*)?$")]
    private static partial Regex Commit();

    [GeneratedRegex(@"^[A-Za-z0-9._/+-]+$")]
    private static partial Regex RefCharacters();

    [GeneratedRegex(@"^-D[A-Za-z_][A-Za-z0-9_.+-]*(:[A-Z]+)?=[^\r\n]*$")]
    private static partial Regex CMakeDefine();
}
