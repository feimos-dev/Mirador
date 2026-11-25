using System.Numerics;
using Mirador.Core.Xr;
using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// A monitor shown as a flat panel above the dashboard's bar. The desktop program draws it, as a quad layer of its own
/// (<see cref="DesktopLayer"/>); here it is where the lasers meet it. A laser on it is the mouse: the pointer follows
/// where it meets the panel (held still through a click, <see cref="LaserClick"/>), the trigger is the left button, and
/// the thumbstick scrolls.
/// </summary>
internal sealed class DesktopPanel : IDisposable
{
    /// <summary>The panel's width; its height follows the monitor's shape.</summary>
    public const float WidthM = 2.0f;

    /// <summary>The space between the bar's top edge and the panel.</summary>
    private const float GapM = 0.04f;

    /// <summary>How far the thumbstick must be pushed to scroll, and how often it then scrolls a notch.</summary>
    private const float ScrollPush = 0.5f;
    private const double ScrollEverySeconds = 0.12;

    private readonly DesktopMonitor monitor;
    private readonly DesktopLayer layer;
    private readonly float aspect;
    private readonly LaserClick click = new();
    private bool pressed;
    private double nextScroll;

    /// <summary>A panel for <paramref name="monitor"/>, drawn by <paramref name="layer"/>, with the mouse on it.</summary>
    public DesktopPanel(DesktopMonitor monitor, DesktopLayer layer)
    {
        this.monitor = monitor;
        this.layer = layer;
        aspect = (float)monitor.Bounds.Width / monitor.Bounds.Height;
    }

    /// <summary>Places the panel above a bar whose top middle is at <paramref name="bar"/>, and moves the mouse with the laser on it.</summary>
    public void Draw(Pose bar)
    {
        var size = new Vec2(WidthM, WidthM / aspect);
        var up = bar.orientation * Vec3.Up;
        var normal = bar.orientation * Vec3.Forward; // towards the viewer
        var right = Vec3.Cross(up, normal); // the viewer's right
        var centre = bar.position + (up * (GapM + (size.y / 2)));

        // A quad layer shows its picture towards its +Z, with +X to the right: the bar's turned half about its own up.
        var facing = Quaternion.Multiply(bar.orientation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));
        layer.Show(new DesktopPlacement(
            monitor.Handle, [facing.X, facing.Y, facing.Z, facing.W, centre.x, centre.y, centre.z], size.x, size.y));

        // The panel takes the lasers' focus as a button would, which is what draws their beams up to it. In the
        // surface's own space +X is the viewer's left, so its upper left corner is at half the width.
        UI.PushSurface(new Pose(centre, bar.orientation), Vec3.Zero, size);
        UI.ButtonBehavior(new Vec3(size.x / 2, size.y / 2, 0), size, "desktop", out _, out var button, out var focus, out var interactor);
        UI.PopSurface();

        if (focus.IsActive() && interactor != Interactor.None
            && DesktopMaths.PanelPoint(interactor.Start, interactor.End, centre, right, up, size.x, size.y) is { } at)
        {
            var hand = HandOf(interactor.Source);
            var trigger = hand is { } h ? Input.Controller(h).trigger : 0;
            var pointer = click.Pointer(at, trigger, pressed || button.IsActive(), size.x, size.y);
            DesktopMouse.MoveTo(monitor, pointer.U, pointer.V);
            if (button.IsJustActive())
            {
                Press(true);
            }

            Scroll(hand);
        }
        else
        {
            click.Left();
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

        layer.Hide();
    }

    private void Press(bool down)
    {
        pressed = down;
        DesktopMouse.Left(down);
    }

    private static Handed? HandOf(InteractorSource source) =>
        (source & InteractorSource.ControllerLeft) != 0 ? Handed.Left
        : (source & InteractorSource.ControllerRight) != 0 ? Handed.Right
        : null;

    /// <summary>The thumbstick of the hand whose laser is on the panel turns the wheel, a notch at a time.</summary>
    private void Scroll(Handed? hand)
    {
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
            DesktopMouse.Scroll(push > 0 ? 1 : -1);
        }
    }
}
