using System.Numerics;

namespace Mirador.Core.Xr;

public readonly record struct DesktopRect(int Left, int Top, int Width, int Height);

public static class DesktopMaths
{
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

    public static (int X, int Y) Absolute(float u, float v, DesktopRect monitor, DesktopRect all)
    {
        var x = monitor.Left + (Math.Clamp(u, 0, 1) * (monitor.Width - 1));
        var y = monitor.Top + (Math.Clamp(v, 0, 1) * (monitor.Height - 1));
        return (Scale(x - all.Left, all.Width), Scale(y - all.Top, all.Height));
    }

    private static int Scale(float pixel, int size) =>
        size <= 1 ? 0 : (int)Math.Clamp(MathF.Round(pixel * 65535f / (size - 1)), 0, 65535);
}
