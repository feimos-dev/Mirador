using System.Text.Json.Serialization;

namespace Mirador.Core.Profiles;

public sealed class ProfileStore(string path)
{
    public ProfileSet Load()
    {
        var leftOut = false;
        var set = JsonFile.Load(path, ProfilesJsonContext.Default.ProfileSet, s => Usable(s, out leftOut));
        if (set is null)
        {
            return ProfileSet.Defaults();
        }

        if (leftOut)
        {
            JsonFile.KeepCopyAsBad(path);
        }

        return set;
    }

    public void Save(ProfileSet set) =>
        JsonFile.Save(path, set, ProfilesJsonContext.Default.ProfileSet);

    private static ProfileSet? Usable(ProfileSet set, out bool leftOut)
    {
        leftOut = false;
        if (set.Profiles is not { Count: > 0 })
        {
            return null;
        }

        var ids = new HashSet<string>();
        var profiles = new List<Profile>();
        foreach (var p in set.Profiles)
        {
            if (p is null || string.IsNullOrEmpty(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Monado is null || !ids.Add(p.Id))
            {
                leftOut = true;
                continue;
            }

            var variables = (p.ExtraEnvironment ?? []).Where(v => v?.Name is not null).Select(v => v with { Value = v.Value ?? "" }).ToList();
            var options = (p.ModuleOptions ?? new Dictionary<string, string>()).Where(o => o.Value is not null).ToDictionary(o => o.Key, o => o.Value);
            leftOut |= variables.Count != (p.ExtraEnvironment?.Count ?? 0) || options.Count != (p.ModuleOptions?.Count ?? 0);
            profiles.Add(p with { ExtraEnvironment = variables, ModuleOptions = options });
        }

        if (profiles.Count == 0)
        {
            return null;
        }

        return set with { Profiles = profiles, SelectedId = set.SelectedId is not null && ids.Contains(set.SelectedId) ? set.SelectedId : profiles[0].Id };
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ProfileSet))]
internal sealed partial class ProfilesJsonContext : JsonSerializerContext;
