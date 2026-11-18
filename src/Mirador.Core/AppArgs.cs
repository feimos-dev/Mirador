using Mirador.Core.Elevation;
using Mirador.Core.OpenVr;
using Mirador.Core.Runtime;
using Mirador.Core.SignIn;

namespace Mirador.Core;

/// <summary>What <c>Mirador.exe</c> was started for.</summary>
/// <param name="Start">Start Monado (<c>--start</c>): the tray's Start from a second Mirador, or a shortcut.</param>
/// <param name="Background">Start without the window (<c>--background</c>): the stand-in, or Windows' startup.</param>
/// <param name="SignIn"><c>--sign-in</c>: Windows started Mirador at sign-in, which may mean no window (App settings, "Start minimized").</param>
/// <param name="RemoveSignIn"><c>--remove-sign-in</c>: the uninstaller's step that stops Mirador starting at sign-in.</param>
/// <param name="AllowElevatedMonado"><c>--allow-elevated-monado</c>: the elevated step (and the installer's) that records this user's consent to run Monado as administrator.</param>
/// <param name="ApproveBuild">
/// <c>--approve-build &lt;profile&gt; &lt;commit&gt; &lt;install folder&gt; [NAME=VALUE…]</c>: the elevated step that approves a
/// profile's build and its variables, which follow as <c>NAME=VALUE</c> arguments up to the first argument that isn't one.
/// For the bundled Monado the commit and the folder are both <c>bundled</c>: only the variables are approved. Null
/// without it, and with fewer values.
/// </param>
public sealed record AppArgs(
    bool Start, bool Background, bool SignIn = false, bool RemoveSignIn = false, bool AllowElevatedMonado = false,
    (string ProfileId, string Commit, string Source, IReadOnlyList<KeyValuePair<string, string>> Variables)? ApproveBuild = null)
{
    /// <summary>The commit and the folder of an approval of the bundled Monado's variables.</summary>
    public const string Bundled = "bundled";

    /// <summary>Reads the command line; flags are matched without regard to case, and anything else is ignored.</summary>
    public static AppArgs Parse(string[] args)
    {
        bool start = false, background = false;
        var signIn = false;
        var removeSignIn = false;
        var allowElevatedMonado = false;
        (string, string, string, IReadOnlyList<KeyValuePair<string, string>>)? approveBuild = null;
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
                    var variables = new List<KeyValuePair<string, string>>();
                    while (i + 1 < args.Length && args[i + 1].IndexOf('=', StringComparison.Ordinal) is > 0 and var at)
                    {
                        variables.Add(new(args[i + 1][..at], args[i + 1][(at + 1)..]));
                        i++;
                    }

                    approveBuild = (profileId, commit, source, variables);
                    break;
            }
        }

        return new AppArgs(start, background, signIn, removeSignIn, allowElevatedMonado, approveBuild);
    }

    /// <summary>The flag of the approval step.</summary>
    public const string ApproveBuildFlag = "--approve-build";
}
