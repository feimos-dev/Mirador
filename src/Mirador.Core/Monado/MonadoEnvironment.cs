using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Monado;

public static class MonadoEnvironment
{
    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> inherited,
        MonadoSettings settings,
        Profile profile,
        IReadOnlyList<EnvVar> moduleFixed,
        bool controllersOffAtStop = false)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in inherited)
        {
            Set(env, name, value);
        }

        foreach (var name in ClearedEnvironment.Names)
        {
            env.Remove(name);
        }

        MonadoSettingsEnvironment.Write(
            (name, value) => Set(env, name, value),
            settings.LighthouseWaitMs,
            settings.RenderScalePercent,
            settings.CompositorMinTimeMs,
            controllersOffAtStop);

        foreach (var v in moduleFixed)
        {
            Set(env, v.Name, v.Value);
        }

        foreach (var v in profile.ExtraEnvironment)
        {
            Set(env, v.Name, v.Value);
        }

        return env;
    }

    private static void Set(Dictionary<string, string> env, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        env.Remove(name);
        if (!string.IsNullOrEmpty(value))
        {
            env[name] = value;
        }
    }
}
