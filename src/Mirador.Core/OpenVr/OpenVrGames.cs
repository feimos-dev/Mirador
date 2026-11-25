using Mirador.Core.Runtime;

namespace Mirador.Core.OpenVr;

public sealed record OpenVrView(string Runtime, bool OffersOpenComposite, string? Warning, string? Problem);

public sealed class OpenVrGames(OpenVrPaths paths, OpenCompositeInstall install)
{
    public const string SetOpenCompositeLabel = "Set OpenLamina";

    public bool Available => install.IsInstalled;

    public OpenVrView View(bool openXrIsMonado, string? activeOpenXr)
    {
        var state = paths.Read();
        if (state.Problem is not null)
        {
            return new OpenVrView("Unknown", false, null, state.Problem);
        }

        if (state.Target == OpenVrTarget.Monado)
        {
            if (!install.IsInstalled)
            {
                return new OpenVrView(
                    "OpenLamina",
                    false,
                    null,
                    "OpenLamina isn't installed any more, so OpenVR games can't start. Press \"Get OpenLamina\" to install it again.");
            }

            return new OpenVrView(
                "OpenLamina",
                false,
                openXrIsMonado ? null
                : activeOpenXr is null ? "No OpenXR runtime is set, so OpenVR games won't start: set Monado as OpenXR runtime too."
                : $"OpenVR games will go to {RuntimeLine.ManifestName(activeOpenXr)}, not Monado: set Monado as OpenXR runtime too.",
                null);
        }

        if (state.Target == OpenVrTarget.Other && state.First is { } first && OpenVrPaths.SamePath(first, OpenCompositeInstall.LegacyFolder))
        {
            return new OpenVrView(
                "OpenComposite (before OpenLamina)",
                true,
                "OpenVR games still go to the old OpenComposite. Press \"" + SetOpenCompositeLabel + "\" to use OpenLamina instead; Windows' Installed apps removes the old one.",
                null);
        }

        var runtime = state.Target switch
        {
            OpenVrTarget.SteamVr => "SteamVR",
            OpenVrTarget.Other => state.First!,
            _ => "None set",
        };
        return new OpenVrView(runtime, true, null, null);
    }

    public string? UseMonado() =>
        install.IsInstalled ? paths.UseMonado() : "OpenLamina isn't installed yet: press \"Get OpenLamina\" first.";
}
