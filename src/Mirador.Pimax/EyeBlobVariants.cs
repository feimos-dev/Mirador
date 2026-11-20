using Mirador.Ipc;

namespace Mirador.Pimax;

/// <summary>
/// The eye geometry files Pimax Play ships, one per model variant of the eye tracker. Which variant a headset needs
/// isn't known from the hardware.
/// </summary>
public static class EyeBlobVariants
{
    public static IReadOnlyList<string> All => PimaxNames.EyeBlobVariants;

    /// <summary>The variant used while Automatic doesn't know the headset's.</summary>
    public const string Fallback = "K11_RAWH";

    /// <summary>The geometry file's name for <paramref name="variant"/>, such as <c>config_XR5PIMAXDA_K11_RAWH.bin</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="variant"/> isn't one of <see cref="All"/>: it becomes part of a path.</exception>
    public static string FileName(string variant) =>
        All.Contains(variant)
            ? $"config_XR5PIMAXDA_{variant}.bin"
            : throw new ArgumentOutOfRangeException(nameof(variant), variant, "Not a known eye geometry variant.");
}
