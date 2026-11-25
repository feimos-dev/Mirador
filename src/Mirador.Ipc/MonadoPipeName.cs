using System.Text;

namespace Mirador.Ipc;

public static class MonadoPipeName
{
    public const string Prefix = @"\\.\pipe\";
    public const string Suffix = "/monado_comp_ipc";

    public static string NameFor(IReadOnlyDictionary<string, string> env, Func<string, string> throughAnsi)
    {
        var folder = Get(env, "XDG_RUNTIME_DIR") ?? Get(env, "XDG_CACHE_HOME") ?? TempPathFrom(env);

        return Prefix + (Ascii.IsValid(folder) ? folder : throughAnsi(folder)) + Suffix;
    }

    public static string TempPathFrom(IReadOnlyDictionary<string, string> env)
    {
        var path = Get(env, "TMP") ?? Get(env, "TEMP") ?? Get(env, "USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.EndsWith('\\') ? path : path + '\\';
    }

    private static string? Get(IReadOnlyDictionary<string, string> env, string name)
    {
        foreach (var (key, value) in env)
        {
            if (value.Length > 0 && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
