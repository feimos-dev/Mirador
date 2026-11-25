using System.Globalization;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Xr;
using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

internal sealed class DashboardApp
{
    private const float TextM = 0.028f;

    private const float Scale = 2.5f;

    private const int DevicesPerRow = 4;

    private const double ConfirmSeconds = 4;

    private const int LowPercent = 15;

    private static readonly Color LowTint = new(3.2f, 0.5f, 0.4f);

    private readonly Link link;
    private readonly SystemButton button = new();
    private readonly Sprite power = Sprite.FromFile("power.png");
    private readonly Sprite recentre = Sprite.FromFile("recentre.png");
    private readonly Sprite confirm = Sprite.FromFile("confirm.png");
    private readonly Sprite desktop = Sprite.FromFile("desktop.png");
    private readonly TextStyle text = TextStyle.FromFont(Font.Default, TextM, Color.White);
    private readonly TextStyle lowText = TextStyle.FromFont(Font.Default, TextM, new Color(1, 0.3f, 0.25f));
    private readonly Dictionary<InteractorSource, IdHash> focused = [];

    private bool open;

    private bool unplaced;
    private Pose bar;
    private XrExit exits = XrExit.Monado;
    private IReadOnlyList<XrDevice> devices = [];

    private readonly DesktopLayer desktopLayer = new();
    private IReadOnlyList<DesktopMonitor> monitors = [];
    private DesktopPanel? panel;
    private int shown = -1;
    private bool desktopFailed;

    private MonadoPose? recentring;
    private MonadoPose? recentredHead;
    private double placeBy;

    private static readonly Vec3 GrabSize = new(0.22f, 0.022f, 0.012f);
    private static readonly Vec2 GrabHitSize = new(GrabSize.x + 0.04f, GrabSize.y + 0.05f);
    private const float GrabGapM = 0.02f;
    private readonly Mesh grab = Mesh.GenerateRoundedCube(GrabSize, 0.0105f, 2);
    private readonly LaserButton[] grabButtons = [new(), new()];
    private readonly RayGrab rayGrab = new();
    private Handed? grabbing;
    private Vec3 grabOffset;
    private double confirmUntil;

    public static int Run(Link link)
    {
        var settings = Program.Settings(XrLink.DashboardAppName);
        settings.overlayApp = true;
        settings.overlayPriority = uint.MaxValue;
        settings.omitEmptyFrames = true;
        if (OperatingSystem.IsWindows())
        {
            DesktopMouse.UseRealPixels();
        }

        if (!SK.Initialize(settings))
        {
            return 1;
        }

        if (!Backend.OpenXR.ExtEnabled("XR_EXTX_overlay"))
        {
            Console.Error.WriteLine("This OpenXR runtime has no overlay sessions.");
            SK.Shutdown();
            return 4;
        }

        ControllerModels.Follow();
        var app = new DashboardApp(link);
        link.Send(XrLink.Running);
        SK.Run(app.Step);
        app.ShowDesktop(-1);
        app.desktopLayer.Dispose();
        return 0;
    }

    internal DashboardApp(Link link)
    {
        this.link = link;
        var ui = UI.Settings;
        ui.margin *= Scale;
        ui.padding *= Scale;
        ui.gutter *= Scale;
        ui.depth *= Scale;
        ui.rounding *= Scale;
        ui.backplateDepth *= Scale;
        ui.backplateBorder *= Scale;
        UI.Settings = ui;
        UI.ColorScheme = Color.HSV(0.6f, 0.3f, 0.45f);
        Renderer.SkyboxVisible = false;
        Show(false);
    }

    internal void Step()
    {
        while (Program.Next(link, out var word, out var value))
        {
            Take(word, value);
        }

        switch (button.Update(Input.ControllerMenuButton.IsActive(), Time.Total))
        {
            case SystemButtonAction.Toggle when link.IsAlone:
                Show(!open);
                break;
            case SystemButtonAction.Toggle:
                link.Send(XrLink.Toggle);
                break;
            case SystemButtonAction.Recentre:
                AskRecentre();
                break;
        }

        if (open && unplaced && HeadPose.Known())
        {
            bar = ToPose(DashboardMaths.AheadOfHead(FromPose(Input.Head)));
            unplaced = false;
        }

        if (recentredHead is { } after && (Arrived(after) || Time.Total >= placeBy))
        {
            bar = ToPose(DashboardMaths.AheadOfHead(after));
            recentredHead = null;
        }

        if (desktopLayer.Failed && !desktopFailed)
        {
            ShowDesktop(-1);
            desktopFailed = true;
            monitors = [];
        }

        if (open && !unplaced)
        {
            DrawGrab(DrawBar());
            try
            {
                panel?.Draw(bar);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"The desktop panel closed: {e.Message}");
                ShowDesktop(-1);
            }

            TickOnHover();
        }
    }

    internal void Take(string word, string value)
    {
        switch (word)
        {
            case XrLink.Open when XrLink.ParseFlag(value) is { } on:
                Show(on);
                break;
            case XrLink.Devices:
                devices = XrLink.ParseDevices(value);
                break;
            case XrLink.Exits:
                exits = XrLink.ParseExits(value);
                confirmUntil = 0;
                break;
            case XrLink.Recentred:
                Recentred(XrLink.ParseFlag(value) == true);
                break;
        }
    }

    private void Show(bool on)
    {
        if (on && !open)
        {
            unplaced = true;
            confirmUntil = 0;
            focused.Clear();
            monitors = OperatingSystem.IsWindows() && !desktopFailed ? DesktopMouse.Monitors() : [];
        }
        else if (!on)
        {
            ShowDesktop(-1);
            recentredHead = null;
        }

        open = on;
        Input.HandVisible(Handed.Max, on);
        Interaction.DefaultInteractors = on ? DefaultInteractors.Controllers : DefaultInteractors.None;
        Interaction.DefaultDraw = on;
    }

    private float DrawBar()
    {
        UI.PushTextStyle(text);
        var pose = bar;
        UI.WindowBegin("bar", ref pose, UIWin.Body, UIMove.None);

        UI.Label(DateTime.Now.ToString("t", CultureInfo.CurrentCulture));
        UI.SameLine();
        UI.HSpace(UI.Settings.gutter);
        if (UI.ButtonRound("recentre", recentre, UI.LineHeight))
        {
            AskRecentre();
        }

        for (var i = 0; i < monitors.Count; i++)
        {
            UI.SameLine();
            UI.PushTint(i == shown ? new Color(1.6f, 1.6f, 1.6f) : Color.White);
            var pressed = monitors.Count == 1
                ? UI.ButtonRound("desktop", desktop, UI.LineHeight)
                : UI.ButtonImg((i + 1).ToString(CultureInfo.CurrentCulture), desktop, UIBtnLayout.Left);
            UI.PopTint();
            if (pressed)
            {
                ShowDesktop(i == shown ? -1 : i);
            }
        }

        if (exits != XrExit.Nothing)
        {
            UI.SameLine();
            DrawExit();
        }

        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            if (i % DevicesPerRow != 0)
            {
                UI.SameLine();
                UI.HSpace(UI.Settings.gutter);
            }

            var low = device.Percent <= LowPercent;
            if (low)
            {
                UI.PushTextStyle(lowText);
            }

            UI.PushId(i);
            UI.Label(device.Percent is { } percent ? $"{device.Label}: {percent.ToString(CultureInfo.CurrentCulture)}%" : device.Label);
            UI.PopId();
            if (low)
            {
                UI.PopTextStyle();
            }
        }

        UI.WindowEnd();
        UI.PopTextStyle();

        var window = UI.LayoutLast;
        return window.center.y - (window.dimensions.y / 2);
    }

    private void ShowDesktop(int index)
    {
        panel?.Dispose();
        panel = null;
        shown = -1;
        if (index >= 0 && index < monitors.Count)
        {
            panel = new DesktopPanel(monitors[index], desktopLayer);
            shown = index;
        }
    }

    private void DrawExit()
    {
        if (exits == XrExit.Game)
        {
            if (UI.ButtonRound("exit", Sprite.Close, UI.LineHeight))
            {
                link.Send(XrLink.Exit);
            }
        }
        else if (Time.Total < confirmUntil)
        {
            UI.PushTint(LowTint);
            if (UI.ButtonRound("confirm", confirm, UI.LineHeight))
            {
                confirmUntil = 0;
                link.Send(XrLink.Exit);
                if (link.IsAlone)
                {
                    SK.Quit();
                }
            }

            UI.PopTint();
        }
        else if (UI.ButtonRound("exit", power, UI.LineHeight))
        {
            confirmUntil = Time.Total + ConfirmSeconds;
        }
    }

    private void AskRecentre()
    {
        if (recentring is not null || World.OriginMode != OriginMode.Stage || !HeadPose.Known())
        {
            return;
        }

        var head = FromPose(Input.Head);
        recentring = link.IsAlone ? null : head;
        link.Send(XrLink.FormatRecentre([head.Qx, head.Qy, head.Qz, head.Qw, head.X, head.Y, head.Z]));
    }

    private void Recentred(bool moved)
    {
        if (recentring is { } head && moved)
        {
            recentredHead = DashboardMaths.HeadAfterRecentre(head);
            placeBy = Time.Total + 0.5;
            Input.HapticPulse(InputHaptic.LController, 0, 0.6f, 0.1f);
            Input.HapticPulse(InputHaptic.RController, 0, 0.6f, 0.1f);
        }

        recentring = null;
    }

    private static bool Arrived(MonadoPose head)
    {
        var forward = Flat(Input.Head.orientation * Vec3.Forward);
        var expected = Flat(new Quat(head.Qx, head.Qy, head.Qz, head.Qw) * Vec3.Forward);
        return Vec3.Distance(Input.Head.position, new Vec3(head.X, head.Y, head.Z)) < 0.1f && Vec3.Dot(forward, expected) > 0.96f;

        static Vec3 Flat(Vec3 v) => new Vec3(v.x, 0, v.z).Normalized;
    }

    private void DrawGrab(float bottom)
    {
        var offset = new Vec3(0, bottom - GrabGapM - (GrabSize.y / 2), 0);
        var centre = bar.position + (bar.orientation * offset);

        UI.PushSurface(new Pose(centre, bar.orientation), Vec3.Zero, GrabHitSize);
        UI.ButtonBehavior(new Vec3(GrabHitSize.x / 2, GrabHitSize.y / 2, 0), GrabHitSize, "grab", out _, out _, out var focus, out _);
        UI.PopSurface();

        var up = bar.orientation * Vec3.Up;
        var right = Vec3.Cross(up, bar.orientation * Vec3.Forward);
        foreach (var hand in new[] { Handed.Left, Handed.Right })
        {
            var laser = Lasers.Of(hand);
            var on = laser is { } l && DesktopMaths.PanelPoint(l.Start, l.End, centre, right, up, GrabHitSize.x, GrabHitSize.y) is not null;
            if (!grabButtons[(int)hand].Update(Input.Controller(hand).trigger, on))
            {
                continue;
            }

            if (grabButtons[(int)hand].Down && grabbing is null && laser is { } ray)
            {
                var direction = (ray.End - ray.Start).Normalized;
                var along = Vec3.Dot(centre - ray.Start, direction);
                grabOffset = bar.orientation.Inverse * (centre - (ray.Start + (direction * along)));
                rayGrab.Start(along, Vec3.Distance(Input.Head.position, ray.Start));
                grabbing = hand;
            }
            else if (!grabButtons[(int)hand].Down && grabbing == hand)
            {
                grabbing = null;
            }
        }

        if (grabbing is { } held && Lasers.Of(held) is { } holding && (holding.End - holding.Start).LengthSq > 1e-8f)
        {
            var direction = (holding.End - holding.Start).Normalized;
            var along = rayGrab.Update(Vec3.Distance(Input.Head.position, holding.Start), Input.Controller(held).stick.y, Time.Stepf);
            var hit = holding.Start + (direction * along);

            var towards = Input.Head.position - (hit + (bar.orientation * grabOffset));
            towards.y = 0;
            var turn = towards.LengthSq > 1e-6f ? Quat.LookDir(towards.Normalized) : bar.orientation;
            centre = hit + (turn * grabOffset);
            bar = new Pose(centre - (turn * offset), turn);
        }

        var lit = grabbing is not null || focus.IsActive();
        grab.Draw(Default.MaterialUI, new Pose(centre, bar.orientation).ToMatrix(), lit ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.45f, 0.45f, 0.45f));
    }

    private void TickOnHover()
    {
        foreach (var interactor in Interactor.All)
        {
            var source = interactor.Source;
            var hand = (source & InteractorSource.ControllerLeft) != 0 ? InputHaptic.LController
                : (source & InteractorSource.ControllerRight) != 0 ? InputHaptic.RController
                : (InputHaptic?)null;
            if (hand is null)
            {
                continue;
            }

            var now = interactor.Focused;
            if (!focused.TryGetValue(source, out var before) || !before.Equals(now))
            {
                focused[source] = now;
                if (!now.Equals(IdHash.None))
                {
                    Input.HapticPulse(hand.Value, 0, 0.15f, 0.01f);
                }
            }
        }
    }

    private static MonadoPose FromPose(Pose pose) => new(
        pose.orientation.x, pose.orientation.y, pose.orientation.z, pose.orientation.w,
        pose.position.x, pose.position.y, pose.position.z);

    private static Pose ToPose(MonadoPose pose) => new(new Vec3(pose.X, pose.Y, pose.Z), new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw));
}
