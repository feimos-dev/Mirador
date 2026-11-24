using Mirador.Core.Xr;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// A monitor shown as a flat panel above the dashboard's bar. A laser on it is the mouse: the pointer follows where it
/// meets the panel, the trigger is the left button, and the thumbstick scrolls.
/// </summary>
internal sealed class DesktopPanel : IDisposable
{
    /// <summary>The panel's width; its height follows the monitor's shape.</summary>
    public const float WidthM = 1.4f;

    /// <summary>The space between the bar's top edge and the panel.</summary>
    private const float GapM = 0.04f;

    /// <summary>How far the thumbstick must be pushed to scroll, and how often it then scrolls a notch.</summary>
    private const float ScrollPush = 0.5f;
    private const double ScrollEverySeconds = 0.12;

    private readonly IDesktopFrames frames;
    private readonly Action<float, float>? moveTo;
    private readonly Action<bool>? left;
    private readonly Action<int>? scroll;
    private readonly Mesh quad = Mesh.GeneratePlane(Vec2.One, Vec3.Forward, Vec3.Up);
    private readonly Material material = Default.MaterialUnlit.Copy();
    private readonly Tex picture = new(TexType.ImageNomips | TexType.Dynamic, TexFormat.Bgra32Srgb);
    private byte[] pixels = [];
    private float aspect;
    private bool pressed;
    private double nextScroll;

    /// <summary>A panel for <paramref name="monitor"/>, with the mouse on it.</summary>
    /// <exception cref="Exception">Windows can't capture the monitor.</exception>
    public DesktopPanel(DesktopMonitor monitor)
        : this(new DesktopCapture(monitor), (float)monitor.Bounds.Width / monitor.Bounds.Height, (u, v) => DesktopMouse.MoveTo(monitor, u, v), DesktopMouse.Left, DesktopMouse.Scroll)
    {
    }

    /// <summary>A panel that shows <paramref name="frames"/>, <paramref name="aspect"/> times as wide as high; without a mouse when the three actions are null.</summary>
    internal DesktopPanel(IDesktopFrames frames, float aspect, Action<float, float>? moveTo = null, Action<bool>? left = null, Action<int>? scroll = null)
    {
        this.frames = frames;
        this.aspect = aspect;
        this.moveTo = moveTo;
        this.left = left;
        this.scroll = scroll;
        material[MatParamName.DiffuseTex] = picture;
    }

    /// <summary>Draws the panel above a bar whose top middle is at <paramref name="bar"/>, and moves the mouse with the laser on it.</summary>
    public void Draw(Pose bar)
    {
        if (frames.TryRead(ref pixels, out var width, out var height))
        {
            unsafe
            {
                fixed (byte* data = pixels)
                {
                    // Copied and uploaded off the frame loop; the panel shows the last picture until it is there.
                    picture.SetColors(width, height, (IntPtr)data, TexFormat.Bgra32Srgb, TexData.CompressNone | TexData.Opaque);
                }
            }

            aspect = (float)width / height;
        }

        var size = new Vec2(WidthM, WidthM / aspect);
        var up = bar.orientation * Vec3.Up;
        var normal = bar.orientation * Vec3.Forward; // towards the viewer
        var right = Vec3.Cross(up, normal); // the viewer's right
        var centre = bar.position + (up * (GapM + (size.y / 2)));
        quad.Draw(material, Matrix.TRS(centre, bar.orientation, new Vec3(size.x, size.y, 1)));

        // The panel takes the lasers' focus as a button would, which is what draws their beams up to it. In the
        // surface's own space +X is the viewer's left, so its upper left corner is at half the width.
        UI.PushSurface(new Pose(centre, bar.orientation), Vec3.Zero, size);
        UI.ButtonBehavior(new Vec3(size.x / 2, size.y / 2, 0), size, "desktop", out _, out var button, out var focus, out var interactor);
        UI.PopSurface();

        if (focus.IsActive() && interactor != Interactor.None
            && DesktopMaths.PanelPoint(interactor.Start, interactor.End, centre, right, up, size.x, size.y) is { } at)
        {
            moveTo?.Invoke(at.U, at.V);
            if (button.IsJustActive())
            {
                Press(true);
            }

            Scroll(interactor.Source);
        }

        // Let go wherever the laser is by then: a drag that leaves the panel must still end.
        if (pressed && !button.IsActive())
        {
            Press(false);
        }
    }

    public void Dispose()
    {
        if (pressed)
        {
            Press(false);
        }

        frames.Dispose();
    }

    private void Press(bool down)
    {
        pressed = down;
        left?.Invoke(down);
    }

    /// <summary>The thumbstick of the hand whose laser is on the panel turns the wheel, a notch at a time.</summary>
    private void Scroll(InteractorSource source)
    {
        var hand = (source & InteractorSource.ControllerLeft) != 0 ? Handed.Left
            : (source & InteractorSource.ControllerRight) != 0 ? Handed.Right
            : (Handed?)null;
        if (hand is null)
        {
            return;
        }

        var push = Input.Controller(hand.Value).stick.y;
        if (MathF.Abs(push) < ScrollPush)
        {
            nextScroll = 0;
        }
        else if (Time.Total >= nextScroll)
        {
            nextScroll = Time.Total + ScrollEverySeconds;
            scroll?.Invoke(push > 0 ? 1 : -1);
        }
    }
}
