using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Runtime;

public static class RuntimeSwitch
{
    public static async Task<string?> SetAsync(
        MonadoInstall install, IHelperClient helper, OpenXrRuntime runtime, string? approvedProfileId = null, bool hadApproval = false)
    {
        if (!install.IsBundled)
        {
            if (approvedProfileId is null)
            {
                return hadApproval
                    ? "This build changed since it was approved. Set it again to approve it."
                    : "This build isn't approved. Set it again to approve it.";
            }

            var approved = await helper.SendAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: approvedProfileId), CancellationToken.None).ConfigureAwait(false);
            return approved.Ok ? null : approved.Error ?? "Mirador's helper service couldn't set the OpenXR runtime, so the runtime is unchanged.";
        }

        var reply = await helper.SendAsync(HelperCommand.SetRuntimeBundled, CancellationToken.None).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return reply.Error ?? "Mirador's helper service couldn't set the OpenXR runtime, so the runtime is unchanged.";
        }

        return runtime.IsActive(install.Manifest)
            ? null
            : $"Mirador's helper service made another Monado the OpenXR runtime ({Describe(runtime.Active())}), not this Mirador's ({install.Manifest}): the helper belongs to another Mirador install. Reinstall Mirador, or pick a Monado build folder in the profile.";
    }

    private static string Describe(string? active) => active ?? "none";
}
