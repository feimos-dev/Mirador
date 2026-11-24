using System.Numerics;

namespace Mirador.Core.Xr;

/// <summary>A rectangle of the Windows desktop in pixels: a monitor, or the whole virtual desktop.</summary>
public readonly record struct DesktopRect(int Left, int Top, int Width, int Height);

/// <summary>The arithmetic of the dashboard's desktop panel: where a laser meets it, and where that is on the desktop.</summary>
public static class DesktopMaths
{
    /// <summary>A captured frame wider than this is halved before it is read back from the GPU.</summary>
    public const int FullWidth = 2560;

    /// <summary>
    /// Where the ray from <paramref name="start"/> through <paramref name="end"/> meets a flat panel, as fractions of
    /// its width and height from its top left corner; null when the ray runs along the panel, points away from it or
    /// misses it. The panel is given by its centre and the directions of its right and top edges (unit length).
    /// </summary>
    public static (float U, float V)? PanelPoint(Vector3 start, Vector3 end, Vector3 centre, Vector3 right, Vector3 up, float width, float height)
    {
        var direction = end - start;
        var normal = Vector3.Cross(right, up);
        var towards = Vector3.Dot(direction, normal);
        if (MathF.Abs(towards) < 1e-6f || width <= 0 || height <= 0)
        {
            return null;
        }

        var along = Vector3.Dot(centre - start, normal) / towards;
        if (along is < 0 or > 1)
        {
            return null;
        }

        var hit = start + (direction * along) - centre;
        var u = (Vector3.Dot(hit, right) / width) + 0.5f;
        var v = 0.5f - (Vector3.Dot(hit, up) / height);
        return u is < 0 or > 1 || v is < 0 or > 1 ? null : (u, v);
    }

    /// <summary>
    /// The point (<paramref name="u"/>, <paramref name="v"/>) of <paramref name="monitor"/> as Windows takes an
    /// absolute mouse position over the whole virtual desktop: 0 to 65535 along each side of <paramref name="all"/>.
    /// </summary>
    public static (int X, int Y) Absolute(float u, float v, DesktopRect monitor, DesktopRect all)
    {
        var x = monitor.Left + (Math.Clamp(u, 0, 1) * (monitor.Width - 1));
        var y = monitor.Top + (Math.Clamp(v, 0, 1) * (monitor.Height - 1));
        return (Scale(x - all.Left, all.Width), Scale(y - all.Top, all.Height));
    }

    /// <summary>
    /// The size a frame of <paramref name="width"/> by <paramref name="height"/> is read back at, and whether that is
    /// half its own: the full frame up to <see cref="FullWidth"/> wide, half of it beyond.
    /// </summary>
    public static (int Width, int Height, bool Halved) Readback(int width, int height) =>
        width > FullWidth ? (Math.Max(1, width / 2), Math.Max(1, height / 2), true) : (width, height, false);

    private static int Scale(float pixel, int size) =>
        size <= 1 ? 0 : (int)Math.Clamp(MathF.Round(pixel * 65535f / (size - 1)), 0, 65535);
}
