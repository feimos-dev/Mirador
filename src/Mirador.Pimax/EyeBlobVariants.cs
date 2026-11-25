using Mirador.Ipc;

namespace Mirador.Pimax;

public static class EyeBlobVariants
{
    public static IReadOnlyList<string> All => PimaxNames.EyeBlobVariants;

    public const string Fallback = "K11_RAWH";

    public static string FileName(string variant) =>
        All.Contains(variant)
            ? $"config_XR5PIMAXDA_{variant}.bin"
            : throw new ArgumentOutOfRangeException(nameof(variant), variant, "Not a known eye geometry variant.");
}
