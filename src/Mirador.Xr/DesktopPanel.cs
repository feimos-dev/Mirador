using System.Numerics;
using Mirador.Core.Xr;
using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// A monitor shown as a flat panel above the dashboard's bar. The desktop program draws it, as a quad layer of its own
/// (<see cref="DesktopLayer"/>); here it is where the lasers meet it. A laser on it is the mouse: the pointer follows
/// where it meets the panel (held still through a click, <see cref="LaserClick"/>), the trigger is the left button
/// (<see cref="LaserButton"/>), and the thumbstick scrolls.
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
    private readonly LaserButton[] buttons = [new(), new()]; // each hand's trigger, by Handed
    private Handed? holding; // the hand whose trigger holds the left button
    private Handed? pointing; // the hand whose laser moves the pointer
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

        // The panel takes the lasers' focus as a button would, which is what draws their beams up to it; the button
        // itself is the triggers', as a StereoKit button lets go of a press whose ray moves on. In the surface's own
        // space +X is the viewer's left, so its upper left corner is at half the width.
        UI.PushSurface(new Pose(centre, bar.orientation), Vec3.Zero, size);
        UI.ButtonBehavior(new Vec3(size.x / 2, size.y / 2, 0), size, "desktop", out _, out _, out var focus, out var interactor);
        UI.PopSurface();

        // Where each controller's laser meets the panel, if it does.
        var hits = new (float U, float V)?[2];
        foreach (var laser in Interactor.All)
        {
            if (laser.Type == InteractorType.Line && laser.Tracked.IsActive() && HandOf(laser.Source) is { } laserHand)
            {
                hits[(int)laserHand] ??= DesktopMaths.PanelPoint(laser.Start, laser.End, centre, right, up, size.x, size.y);
            }
        }

        // Each trigger, every frame: the first one pulled with its laser on the panel holds the left button until it is
        // let go, wherever its laser is by then, so a drag that leaves the panel still ends.
        var held = holding;
        bool? press = null;
        foreach (var each in new[] { Handed.Left, Handed.Right })
        {
            var button = buttons[(int)each];
            if (!button.Update(Input.Controller(each).trigger, hits[(int)each] is not null))
            {
                continue;
            }

            if (button.Down && holding is null)
            {
                (holding, press) = (each, true);
            }
            else if (!button.Down && holding == each)
            {
                (holding, press) = (null, false);
            }
        }

        // The pointer follows the hand that held the button as the frame began (a release lands where it was), else one
        // on the panel whose trigger is moving, else the laser StereoKit focuses, else either on the panel.
        var focused = focus.IsActive() && interactor != Interactor.None ? HandOf(interactor.Source) : null;
        Handed? Pulling(Handed each) => hits[(int)each] is not null && Input.Controller(each).trigger > LaserClick.RestTrigger ? each : null;
        var hand = held ?? holding ?? Pulling(Handed.Left) ?? Pulling(Handed.Right)
            ?? (focused is { } f && hits[(int)f] is not null ? f
            : hits[(int)Handed.Left] is not null ? Handed.Left
            : hits[(int)Handed.Right] is not null ? Handed.Right
            : null);
        if (hand != pointing)
        {
            click.Left();
            pointing = hand;
        }

        layer.Point(hand is { } on && hits[(int)on] is not null ? on : null);
        if (hand is { } h && hits[(int)h] is { } at)
        {
            var pointer = click.Pointer(at, Input.Controller(h).trigger, held is not null || holding is not null, size.x, size.y);
            DesktopMouse.MoveTo(monitor, pointer.U, pointer.V);
            Scroll(h);
        }

        if (press is { } down)
        {
            DesktopMouse.Left(down);
        }
    }

    public void Dispose()
    {
        if (holding is not null)
        {
            DesktopMouse.Left(false);
        }

        layer.Hide();
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
