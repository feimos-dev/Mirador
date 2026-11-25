using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

public static class ElevatedRequestCheck
{
    public const string NotAllowed = "Monado may run as administrator only after you allowed it: turn on the profile's Run as administrator and save it.";
    public const string NotMirador = "Only Mirador may start Monado as administrator.";
    public const string NoAdmin = "Running Monado as administrator needs an administrator account.";
    public const string AlreadyRunning = "Monado already runs as administrator.";

    public const string NotTheUser = "Only the signed-in user may start Monado as administrator.";

    public const string VariablesChanged = "This profile's variables changed since they were approved. Press Start to approve them.";

    public const string VariablesNotApproved = "This profile's variables aren't approved to run as administrator. Press Start to approve them.";

    public static string? ApprovedVariables(
        ElevatedStart start, string owner, IApprovedBuildStore? store, out IReadOnlyList<KeyValuePair<string, string>> variables)
    {
        variables = [];
        if (start.Variables is not { Count: > 0 } asked)
        {
            return null;
        }

        if (ElevatedVariables.FirstRefused(asked) is { } refused)
        {
            return $"{refused} can't be set for Monado run as administrator.";
        }

        if (store is null || !ApprovedBuilds.IsProfileId(start.ProfileId) || store.Get(owner, start.ProfileId) is not { } record)
        {
            return VariablesNotApproved;
        }

        var forThisMonado = string.IsNullOrEmpty(start.Build)
            ? record.Commit == ApprovedBuilds.BundledCommit
            : start.Build == start.ProfileId && record.Commit != ApprovedBuilds.BundledCommit;
        if (!forThisMonado)
        {
            return VariablesNotApproved;
        }

        if (ElevatedVariables.FirstRefused(record.Variables) is { } refusedInRecord)
        {
            return $"{refusedInRecord} can't be set for Monado run as administrator.";
        }

        if (!Same(asked, record.Variables))
        {
            return VariablesChanged;
        }

        variables = record.Variables;
        return null;
    }

    private static bool Same(IReadOnlyList<KeyValuePair<string, string>> asked, IReadOnlyList<KeyValuePair<string, string>> approved) =>
        asked.Count == approved.Count
        && asked.Zip(approved).All(pair =>
            string.Equals(pair.First.Key, pair.Second.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal));

    public static string? Check(bool consented, string? imagePath, string installDir, bool hasLinkedToken, bool running)
    {
        if (!consented)
        {
            return NotAllowed;
        }

        if (!Mirador.Ipc.InstalledMirador.IsInstalledCopy(imagePath, installDir))
        {
            return NotMirador;
        }

        if (!hasLinkedToken)
        {
            return NoAdmin;
        }

        return running ? AlreadyRunning : null;
    }
}
