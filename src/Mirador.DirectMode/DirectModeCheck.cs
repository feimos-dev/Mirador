using Mirador.Core;
using Mirador.Core.Monado;
using Mirador.Core.Native;

namespace Mirador.DirectMode;

public sealed class DirectModeCheck(Func<string?> driverNow, Func<ProcessSpec, IConsoleProcess> startConsole)
{
    public async Task<string?> EnsureAsync(
        LayerFiles layer,
        string? lastGoodDriver,
        Action<string?> remember,
        Action<DisplayCheckResult>? saveOutput,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var driver = driverNow();
        if (driver == lastGoodDriver)
        {
            return null;
        }

        progress.Report("Checking direct mode with the new NVIDIA driver…");
        var check = await DisplayCheck.RunAsync(layer.CheckExe, startConsole, ct).ConfigureAwait(false);
        saveOutput?.Invoke(check);
        if (check.NotUsed)
        {
            return NotUsedWarning(check.Message);
        }

        if (!check.Pass)
        {
            throw new MiradorException(check.Finished
                ? $"Direct mode didn't pass its check with NVIDIA driver {driver}: {Sentence(check.Message)}{(check.WhatToDo is null ? "" : " " + Sentence(Capitalized(check.WhatToDo)))}"
                : $"Mirador couldn't check direct mode with the new NVIDIA driver {driver}: {Sentence(check.Message)} Try again.");
        }

        remember(driver);
        return null;
    }

    internal static string NotUsedWarning(string checkMessage) =>
        $"Direct mode isn't used for this headset: {Sentence(checkMessage)} Monado shows an image on it only through " +
        "Windows' own headset route (WinRT/WDDC, upstream merge request !2811), which upstream Monado doesn't have yet.";

    public static string Sentence(string s) => s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? s : s + ".";

    public static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
