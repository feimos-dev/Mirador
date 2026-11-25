using System.Numerics;
using Mirador.Core.Xr;
using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

internal sealed class DesktopPanel : IDisposable
{
    public const float WidthM = 2.0f;

    private const float GapM = 0.04f;

    private const float ScrollPush = 0.5f;
    private const double ScrollEverySeconds = 0.12;

    private readonly DesktopMonitor monitor;
    private readonly DesktopLayer layer;
    private readonly float aspect;
    private readonly LaserClick click = new();
    private readonly LaserButton[] buttons = [new(), new()];
    private Handed? holding;
    private Handed? pointing;
    private double nextScroll;

    public DesktopPanel(DesktopMonitor monitor, DesktopLayer layer)
    {
        this.monitor = monitor;
        this.layer = layer;
        aspect = (float)monitor.Bounds.Width / monitor.Bounds.Height;
    }

    public void Draw(Pose bar)
    {
        var size = new Vec2(WidthM, WidthM / aspect);
        var up = bar.orientation * Vec3.Up;
        var normal = bar.orientation * Vec3.Forward;
        var right = Vec3.Cross(up, normal);
        var centre = bar.position + (up * (GapM + (size.y / 2)));

        var facing = Quaternion.Multiply(bar.orientation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));
        layer.Show(new DesktopPlacement(
            monitor.Handle, [facing.X, facing.Y, facing.Z, facing.W, centre.x, centre.y, centre.z], size.x, size.y));

        UI.PushSurface(new Pose(centre, bar.orientation), Vec3.Zero, size);
        UI.ButtonBehavior(new Vec3(size.x / 2, size.y / 2, 0), size, "desktop", out _, out _, out var focus, out var interactor);
        UI.PopSurface();

        var hits = new (float U, float V)?[2];
        foreach (var laser in Interactor.All)
        {
            if (laser.Type == InteractorType.Line && laser.Tracked.IsActive() && Lasers.HandOf(laser.Source) is { } laserHand)
            {
                hits[(int)laserHand] ??= DesktopMaths.PanelPoint(laser.Start, laser.End, centre, right, up, size.x, size.y);
            }
        }

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

        var focused = focus.IsActive() && interactor != Interactor.None ? Lasers.HandOf(interactor.Source) : null;
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
