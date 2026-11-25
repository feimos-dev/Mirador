using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Elevation;

public static class ElevatedChoice
{
    public const string NotInstalledCopy = "Runs as you: this copy of Mirador isn't the installed one.";
    public const string BuildFolder = "Runs as you: this profile uses a build folder or a branch built here.";
    public const string ExtraVariables = "Runs as you: the variables aren't approved. Press Start to approve them.";

    public const string VariablesChanged = "Runs as you: the variables changed. Press Start to approve them.";

    public const string VariablesTooLong = "These variables are too long to run as administrator. Shorten them, or run this profile as you.";

    public static string RefusedVariable(string name) => $"Runs as you: {name} can't be set for Monado as administrator.";
    public const string NoElevatedTable = "Runs as you: Mirador has no administrator settings for this headset yet.";
    public const string NotApproved = "Runs as you: this build isn't approved. Press Start to approve it.";

    public const string BuildChanged = "Runs as you: this build changed since it was approved. Press Start to approve it.";

    public static bool ConsentNeeded(ProfileSet set) => set.Profiles.Any(p => p.RunAsAdministrator);

    public static string? RunsAsYouReason(
        Profile profile, IHeadsetModule? module, bool installedCopy, bool approvedBuild = false, bool variablesApproved = false)
    {
        if (!installedCopy)
        {
            return NotInstalledCopy;
        }

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

    public static bool SameVariables(IReadOnlyList<EnvVar> variables, IReadOnlyList<KeyValuePair<string, string>> approved) =>
        variables.Count == approved.Count
        && variables.Zip(approved).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal));

    public static bool ApprovesOnSave(Profile? saved, Profile edited) =>
        edited.RunAsAdministrator
        && (saved is not { RunAsAdministrator: true } || !saved.ExtraEnvironment.SequenceEqual(edited.ExtraEnvironment));

    public static bool KeepsSwitchWhenDeclined(Profile? saved) => saved is { RunAsAdministrator: true };

    public static IReadOnlyList<KeyValuePair<string, string>> Variables(Profile profile) =>
        [.. profile.ExtraEnvironment.Select(v => new KeyValuePair<string, string>(v.Name, v.Value))];

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
