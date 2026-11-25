using System.Text.Json.Serialization;

namespace Mirador.Core.Profiles;

public sealed record ProfileSet(IReadOnlyList<Profile> Profiles, string SelectedId)
{
    public static ProfileSet Defaults() => new(
        [new Profile("default", "Default", null, BuildSource.Mirador, [], new Dictionary<string, string>())],
        "default");

    [JsonIgnore]
    public Profile Selected => Find(SelectedId);

    public ProfileSet Select(string id)
    {
        Find(id);
        return this with { SelectedId = id };
    }

    public ProfileSet Add(Profile p)
    {
        if (string.IsNullOrEmpty(p.Id) || Profiles.Any(x => x.Id == p.Id))
        {
            throw new ArgumentException("A profile needs an id that no other profile has.", nameof(p));
        }

        return this with { Profiles = [.. Profiles, p with { Name = CheckName(p.Name, exceptId: null) }] };
    }

    public ProfileSet Duplicate(string id, string newName)
    {
        var source = Find(id);
        var copy = source with
        {
            Id = Profile.NewId(),
            Name = CheckName(newName, exceptId: null),
            ExtraEnvironment = [.. source.ExtraEnvironment],
            ModuleOptions = new Dictionary<string, string>(source.ModuleOptions),
        };
        return this with { Profiles = [.. Profiles, copy] };
    }

    public ProfileSet Rename(string id, string name) => Replace(Find(id) with { Name = name });

    public ProfileSet Delete(string id)
    {
        Find(id);
        if (Profiles.Count == 1)
        {
            throw new MiradorException("Mirador needs at least one profile.");
        }

        var rest = Profiles.Where(p => p.Id != id).ToList();
        return this with { Profiles = rest, SelectedId = SelectedId == id ? rest[0].Id : SelectedId };
    }

    public ProfileSet Replace(Profile p)
    {
        Find(p.Id);
        var named = p with { Name = CheckName(p.Name, exceptId: p.Id) };
        return this with { Profiles = [.. Profiles.Select(x => x.Id == p.Id ? named : x)] };
    }

    private Profile Find(string id) =>
        Profiles.FirstOrDefault(p => p.Id == id) ?? throw new MiradorException("That profile no longer exists.");

    private string CheckName(string name, string? exceptId)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new MiradorException("A profile needs a name.");
        }

        if (Profiles.Any(p => p.Id != exceptId && string.Equals(p.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MiradorException($"A profile called {trimmed} already exists.");
        }

        return trimmed;
    }
}
