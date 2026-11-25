using Mirador.Core.Profiles;

namespace Mirador.Pimax;

public static class EyeVariantChoice
{
    public const string OptionKey = "eyeVariant";

    public const string None = "none";

    public static string? Of(Profile profile) =>
        profile.ModuleOptions.GetValueOrDefault(OptionKey) is { } value && (value == None || EyeBlobVariants.All.Contains(value)) ? value : null;

    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> options, string? choice)
    {
        var changed = new Dictionary<string, string>(options);
        if (choice is null)
        {
            changed.Remove(OptionKey);
        }
        else
        {
            changed[OptionKey] = choice;
        }

        return changed;
    }
}
