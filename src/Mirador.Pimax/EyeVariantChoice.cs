using Mirador.Core.Profiles;

namespace Mirador.Pimax;

/// <summary>
/// A profile's eye tracker variant, kept in its module options: Automatic (what <see cref="BlobChoice"/> knows for the
/// tracker), None (no geometry upload), or one of <see cref="EyeBlobVariants.All"/>.
/// </summary>
public static class EyeVariantChoice
{
    public const string OptionKey = "eyeVariant";

    public const string None = "none";

    /// <summary>The profile's choice: <see cref="None"/>, a variant, or null for Automatic (also for a value Mirador doesn't know).</summary>
    public static string? Of(Profile profile) =>
        profile.ModuleOptions.GetValueOrDefault(OptionKey) is { } value && (value == None || EyeBlobVariants.All.Contains(value)) ? value : null;

    /// <summary><paramref name="options"/> with <paramref name="choice"/> set; null (Automatic) takes the option out.</summary>
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
