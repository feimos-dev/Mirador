using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>Decides whether an elevated-start request may go ahead, and if not why.</summary>
public static class ElevatedRequestCheck
{
    public const string NotAllowed = "Monado may run as administrator only after you allowed it: turn on the profile's Run as administrator and save it.";
    public const string NotMirador = "Only Mirador may start Monado as administrator.";
    public const string NoAdmin = "Running Monado as administrator needs an administrator account.";
    public const string AlreadyRunning = "Monado already runs as administrator.";

    /// <summary>The refusal the user should see when the caller's SID isn't the session user's (checked in the runner).</summary>
    public const string NotTheUser = "Only the signed-in user may start Monado as administrator.";

    /// <summary>The refusal when a Start's variables aren't exactly the ones the caller approved for the profile.</summary>
    public const string VariablesChanged = "This profile's variables changed since they were approved. Press Start to approve them.";

    /// <summary>The refusal when the caller has no approval of the profile's variables for the Monado the Start runs.</summary>
    public const string VariablesNotApproved = "This profile's variables aren't approved to run as administrator. Press Start to approve them.";

    /// <summary>
    /// Whether the profile's variables of <paramref name="start"/> may run, and if not why. Never from the request's word:
    /// the approval is read from the helper's own <paramref name="store"/> for <paramref name="owner"/> (the pipe
    /// client's SID) and <see cref="ElevatedStart.ProfileId"/>. It must be for the Monado the Start runs (the approved
    /// build of <see cref="ElevatedStart.Build"/>, or the bundled Monado), and the variables must be exactly the
    /// approved ones: same order, same names (whatever their case, as Windows' names) and same values. Every name is
    /// checked against <see cref="ElevatedVariables.IsRefused"/>, even an approved one. A Start without variables needs
    /// nothing.
    /// </summary>
    /// <param name="variables">The variables to run with: the approved ones, or none.</param>
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

    /// <summary>
    /// The refusal the user should see, or null when the start may go ahead. The checks are, in order: the caller
    /// consented, the caller is the installed <c>Mirador.exe</c>, the caller has an administrator (linked) token, and no
    /// elevated Monado already runs for this caller. The caller's SID being the session user's is checked in the runner,
    /// where the session token is known (<see cref="NotTheUser"/>).
    /// </summary>
    /// <param name="consented">Whether the caller's SID has its value in the consent key.</param>
    /// <param name="imagePath">The caller process's program file, from the pipe (never from the request); null when unknown.</param>
    /// <param name="installDir">The folder the installed <c>Mirador.exe</c> is in (the helper's own: they are installed side by side).</param>
    /// <param name="hasLinkedToken">Whether the caller's token has a linked (elevated) token.</param>
    /// <param name="running">Whether an elevated Monado already runs for this caller.</param>
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
