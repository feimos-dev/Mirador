using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Elevation;

/// <summary>
/// Whether a Start may run Monado as administrator, for a user who allowed it (Mirador.md section 4.10 items 45 and 46):
/// the one test behind both the session's choice of launcher and Home's "Runs as you" line, so the two can't drift.
/// </summary>
public static class ElevatedChoice
{
    public const string NotInstalledCopy = "Runs as you: this copy of Mirador isn't the installed one.";
    public const string BuildFolder = "Runs as you: this profile uses a build folder or a branch built here.";
    public const string ExtraVariables = "Runs as you: this profile's extra environment variables aren't approved. Approve them on the Profiles page.";

    /// <summary>Home's line when the profile's variables differ from the ones approved (added, removed or changed).</summary>
    public const string VariablesChanged = "Runs as you: the variables changed since they were approved. Approve them again on the Profiles page.";

    /// <summary>The approval step's refusal when the variables wouldn't fit in the helper's request.</summary>
    public const string VariablesTooLong = "These variables are too long to run as administrator. Shorten them, or run this profile as you.";

    /// <summary>Why a profile runs as the user when <paramref name="name"/>, one of its variables, may not be set for Monado as administrator.</summary>
    public static string RefusedVariable(string name) => $"Runs as you: {name} can't be set for Monado as administrator.";
    public const string NoElevatedTable = "Runs as you: Mirador has no administrator settings for this headset yet.";
    public const string NotApproved = "Runs as you: the new build runs as you until you approve it on the Profiles page.";

    /// <summary>
    /// Whether the user's consent for Monado as administrator is still needed: while a profile has "Run as administrator"
    /// on. Without one it is taken back, so it never outlives the profiles that asked for it.
    /// </summary>
    public static bool ConsentNeeded(ProfileSet set) => set.Profiles.Any(p => p.RunAsAdministrator);

    /// <summary>
    /// Why this profile runs as the user although it has "Run as administrator" on and the user allowed it, as Home's line; null when
    /// it may run as administrator. In order: this Mirador isn't the installed one (the helper starts Monado only for
    /// <c>Mirador.exe</c> in its own install folder: <see cref="Mirador.Ipc.InstalledMirador"/>), the profile uses a
    /// build folder or a branch built here (anything but Mirador's Monado) that isn't approved, one of its variables may
    /// not be set for Monado as administrator (<see cref="ElevatedVariables.IsRefused"/>), its variables aren't exactly
    /// the approved ones, or its module has no administrator settings for the headset. A profile of Mirador's Monado
    /// without variables needs no approval; one of a branch or build folder needs its variables approved with the build,
    /// none included.
    /// </summary>
    /// <param name="installedCopy">Whether this Mirador is the installed one, as the helper judges it.</param>
    /// <param name="approvedBuild">Whether the profile's current build is the one approved to run as administrator.</param>
    /// <param name="variablesApproved">Whether the profile's variables are exactly the approved ones (<see cref="SameVariables"/>).</param>
    public static string? RunsAsYouReason(
        Profile profile, IHeadsetModule? module, bool installedCopy, bool approvedBuild = false, bool variablesApproved = false)
    {
        if (!installedCopy)
        {
            return NotInstalledCopy;
        }

        // The bundled Monado lives in the admin-only install folder (Mirador.md §4.11 item 55); a branch built here or
        // a build folder is where the user can write, until it is approved.
        if (profile.Monado.Kind != SourceKind.Mirador && !(profile.RunAsAdministrator && approvedBuild))
        {
            return profile.RunAsAdministrator ? NotApproved : BuildFolder;
        }

        if (FirstRefused(profile) is { } refused)
        {
            return RefusedVariable(refused);
        }

        if (!variablesApproved && (profile.ExtraEnvironment.Count > 0 || profile.Monado.Kind != SourceKind.Mirador))
        {
            return ExtraVariables;
        }

        return module is not null && module.ElevatedModuleId is null ? NoElevatedTable : null;
    }

    /// <summary>
    /// Whether the profile's <paramref name="variables"/> are exactly the <paramref name="approved"/> ones, as the helper
    /// checks them: the same number, in the same order, the same names (whatever their case, as Windows' names) and the
    /// same values. Fewer is a difference too.
    /// </summary>
    public static bool SameVariables(IReadOnlyList<EnvVar> variables, IReadOnlyList<KeyValuePair<string, string>> approved) =>
        variables.Count == approved.Count
        && variables.Zip(approved).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal));

    /// <summary>
    /// Whether saving <paramref name="edited"/> over <paramref name="saved"/> approves it (one prompt): "Run as
    /// administrator" turns on, or the variables change while it stays on. Any other edit of a profile with the switch on
    /// asks nothing; it runs as you until approved, and Home says why.
    /// </summary>
    public static bool ApprovesOnSave(Profile? saved, Profile edited) =>
        edited.RunAsAdministrator
        && (saved is not { RunAsAdministrator: true } || !saved.ExtraEnvironment.SequenceEqual(edited.ExtraEnvironment));

    /// <summary>Whether declining the save's prompt keeps "Run as administrator" on: when it was on before the edit.</summary>
    public static bool KeepsSwitchWhenDeclined(Profile? saved) => saved is { RunAsAdministrator: true };

    /// <summary>The profile's variables as the helper takes them.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Variables(Profile profile) =>
        [.. profile.ExtraEnvironment.Select(v => new KeyValuePair<string, string>(v.Name, v.Value))];

    /// <summary>
    /// Why the profile's variables can't be approved, before any prompt: one may not be set for Monado as administrator, or
    /// they wouldn't fit in the helper's request; null when they can.
    /// </summary>
    public static string? ApprovalProblem(Profile profile)
    {
        if (FirstRefused(profile) is { } refused)
        {
            return $"{refused} can't be set for Monado as administrator. Remove it, or run this profile as you.";
        }

        return ElevatedVariables.FitInStart(profile.Id, Variables(profile)) ? null : VariablesTooLong;
    }

    private static string? FirstRefused(Profile profile) => profile.ExtraEnvironment.Select(v => v.Name).FirstOrDefault(ElevatedVariables.IsRefused);
}
