using Mirador.Core.Elevation;
using Mirador.Core.OpenVr;
using Mirador.Core.Runtime;
using Mirador.Core.SignIn;

namespace Mirador.Core;

public sealed record AppArgs(
    bool Start, bool Background, bool SignIn = false, bool RemoveSignIn = false, bool AllowElevatedMonado = false,
    (string ProfileId, string Commit, string Source, IReadOnlyList<KeyValuePair<string, string>> Variables)? ApproveBuild = null,
    (string ProfileId, IReadOnlyList<KeyValuePair<string, string>> Variables)? ApproveVariables = null)
{
    public const string Bundled = "bundled";

    public static AppArgs Parse(string[] args)
    {
        bool start = false, background = false;
        var signIn = false;
        var removeSignIn = false;
        var allowElevatedMonado = false;
        (string, string, string, IReadOnlyList<KeyValuePair<string, string>>)? approveBuild = null;
        (string, IReadOnlyList<KeyValuePair<string, string>>)? approveVariables = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--start":
                    start = true;
                    break;
                case "--background":
                    background = true;
                    break;
                case SignInStartup.Flag:
                    signIn = true;
                    break;
                case SignInStartup.RemoveFlag:
                    removeSignIn = true;
                    break;
                case AllowElevatedCommand.Flag:
                    allowElevatedMonado = true;
                    break;
                case ApproveBuildFlag when i + 3 < args.Length:
                    var (profileId, commit, source) = (args[i + 1], args[i + 2], args[i + 3]);
                    i += 3;
                    approveBuild = (profileId, commit, source, ReadVariables(args, ref i));
                    break;
                case ApproveVariablesFlag when i + 1 < args.Length:
                    var variablesOf = args[++i];
                    approveVariables = (variablesOf, ReadVariables(args, ref i));
                    break;
            }
        }

        return new AppArgs(start, background, signIn, removeSignIn, allowElevatedMonado, approveBuild, approveVariables);
    }

    private static List<KeyValuePair<string, string>> ReadVariables(string[] args, ref int i)
    {
        var variables = new List<KeyValuePair<string, string>>();
        while (i + 1 < args.Length && args[i + 1].IndexOf('=', StringComparison.Ordinal) is > 0 and var at)
        {
            variables.Add(new(args[i + 1][..at], args[i + 1][(at + 1)..]));
            i++;
        }

        return variables;
    }

    public static (string Commit, string Source) ApprovalTarget(string commit, string source)
    {
        if (commit == Bundled)
        {
            return source == Bundled
                ? (Mirador.Ipc.ApprovedBuilds.BundledCommit, "")
                : throw new InvalidOperationException("An approval of the bundled Monado names no build folder.");
        }

        return source == Bundled
            ? throw new InvalidOperationException("A build's approval needs its build folder.")
            : (commit, source);
    }

    public const string ApproveBuildFlag = "--approve-build";

    public const string ApproveVariablesFlag = "--approve-variables";
}
