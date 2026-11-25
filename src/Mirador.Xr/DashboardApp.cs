using System.Globalization;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Xr;
using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// The dashboard: an overlay app, drawn above whatever Monado shows. Open, it is a bar ahead of the head with the time,
/// a recentre button, a button for each monitor, an exit button and each controller's and tracker's battery, pointed
/// at with a laser from either controller; a monitor's button shows the desktop on a panel above the bar, where the
/// laser is the mouse. Closed, it draws nothing. It reads the system button (a press opens or closes it, a hold recentres) and
/// leaves every decision to Mirador: it only says what was pressed and shows what it is told.
/// </summary>
internal sealed class DashboardApp
{
    /// <summary>The height of the bar's letters, read from <see cref="DashboardMaths.AheadM"/> away.</summary>
    private const float TextM = 0.028f;

    /// <summary>How much larger than StereoKit's arm's-length defaults the bar's spacing is.</summary>
    private const float Scale = 2.5f;

    /// <summary>How many devices share a row of the bar before the next row begins.</summary>
    private const int DevicesPerRow = 4;

    /// <summary>How long the exit button waits for its confirmation before it is the exit button again.</summary>
    private const double ConfirmSeconds = 4;

    /// <summary>At or under this charge a battery's number is red.</summary>
    private const int LowPercent = 15;

    /// <summary>The red of the exit button's confirmation: a tint over the button's own colour.</summary>
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

    // Open, but not yet placed: the head wasn't known yet.
    private bool unplaced;
    private Pose bar;
    private XrExit exits = XrExit.Monado;
    private IReadOnlyList<XrDevice> devices = [];

    // The desktop: the monitors as they were when the bar opened, and the one shown above the bar, if any.
    private readonly DesktopLayer desktopLayer = new();
    private IReadOnlyList<DesktopMonitor> monitors = [];
    private DesktopPanel? panel;
    private int shown = -1;
    private bool desktopFailed;

    // A recentre asked for and not yet answered: the head it was asked from. Then, once answered, where the head will be
    // in the moved stage: the bar goes ahead of it when StereoKit's head gets there, or at the latest at the time given.
    private MonadoPose? recentring;
    private MonadoPose? recentredHead;
    private double placeBy;

    // The grab bar under the bar, which moves it.
    private static readonly Vec3 GrabSize = new(0.22f, 0.022f, 0.012f);
    private const float GrabGapM = 0.02f;
    private readonly Mesh grab = Mesh.GenerateRoundedCube(GrabSize, 0.0105f, 2);
    private double confirmUntil;

    public static int Run(Link link)
    {
        var settings = Program.Settings(XrLink.DashboardAppName);
        settings.overlayApp = true;
        settings.overlayPriority = uint.MaxValue; // above every other overlay
        settings.omitEmptyFrames = true; // closed, it submits no layer
        if (OperatingSystem.IsWindows())
        {
            DesktopMouse.UseRealPixels();
        }

        if (!SK.Initialize(settings))
        {
            return 1;
        }

        // StereoKit leaves SK.System.overlayApp unset; the session is an overlay only if the extension was enabled.
        if (!Backend.OpenXR.ExtEnabled("XR_EXTX_overlay"))
        {
            // As a scene app it would take the view from every game.
            Console.Error.WriteLine("This OpenXR runtime has no overlay sessions.");
            SK.Shutdown();
            return 4;
        }

        ControllerModels.Follow();
        var app = new DashboardApp(link);
        link.Send(XrLink.Running);
        SK.Run(app.Step);
        app.ShowDesktop(-1); // lets go of a held mouse button
        app.desktopLayer.Dispose();
        return 0;
    }

    /// <summary>Call once StereoKit is up: sets the bar's look, and starts closed.</summary>
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

    /// <summary>One frame: Mirador's lines, the system button, and the bar when open.</summary>
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

        // StereoKit takes the moved stage when Monado's event comes, which can be a frame or two either side of
        // Mirador's answer: the bar moves once its head is near where the recentre put it.
        if (recentredHead is { } after && (Arrived(after) || Time.Total >= placeBy))
        {
            bar = ToPose(DashboardMaths.AheadOfHead(after));
            recentredHead = null;
        }

        if (desktopLayer.Failed && !desktopFailed)
        {
            // Its log says why: the desktop buttons go.
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

    /// <summary>Does what a line from Mirador says.</summary>
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

    /// <summary>Opens the bar ahead of the head, or closes it; closed, nothing of the dashboard is drawn, the controllers and lasers included.</summary>
    private void Show(bool on)
    {
        if (on && !open)
        {
            // Placed ahead of the head at the first frame it is known.
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

    /// <summary>Draws the bar; its bottom edge in its own space.</summary>
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
            // One button a monitor; with several, each carries its number.
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

            // "L: 82%", in red when low; a device without a battery is its label alone.
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

        // After WindowEnd, the last layout is the whole window's.
        var window = UI.LayoutLast;
        return window.center.y - (window.dimensions.y / 2);
    }

    /// <summary>Shows monitor <paramref name="index"/> above the bar, or (-1) none.</summary>
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

    /// <summary>The exit button: a cross that ends the game shown, or a power sign that stops Monado once its tick is pressed too.</summary>
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

    /// <summary>Asks Mirador to put the origin under the head, turned the way it faces.</summary>
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

    /// <summary>Mirador moved the stage, or couldn't: the open bar goes ahead of where the head looks in the moved stage, and both controllers tick.</summary>
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

    /// <summary>Whether StereoKit's head is where <paramref name="head"/> is: within 10 cm, facing the same way to 15° or so.</summary>
    private static bool Arrived(MonadoPose head)
    {
        var forward = Flat(Input.Head.orientation * Vec3.Forward);
        var expected = Flat(new Quat(head.Qx, head.Qy, head.Qz, head.Qw) * Vec3.Forward);
        return Vec3.Distance(Input.Head.position, new Vec3(head.X, head.Y, head.Z)) < 0.1f && Vec3.Dot(forward, expected) > 0.96f;

        static Vec3 Flat(Vec3 v) => new Vec3(v.x, 0, v.z).Normalized;
    }

    /// <summary>The grab bar <see cref="GrabGapM"/> under the bar's bottom (<paramref name="bottom"/>, in the bar's own space): holding it moves the bar, upright and turned to face the user.</summary>
    private void DrawGrab(float bottom)
    {
        var offset = new Vec3(0, bottom - GrabGapM - (GrabSize.y / 2), 0);
        var handle = new Pose(bar.position + (bar.orientation * offset), bar.orientation);
        // Grabbed in a box larger than the pill: a laser wobbles more than the pill is high.
        if (UI.Handle("grab", ref handle, new Bounds(GrabSize + new Vec3(0.04f, 0.05f, 0.03f)), false, UIMove.PosOnly, UIGesture.Pinch))
        {
            // As AheadOfHead places it: its -Z towards the head along the floor.
            var towards = Input.Head.position - handle.position;
            towards.y = 0;
            var turn = towards.LengthSq > 1e-6f ? Quat.LookDir(towards.Normalized) : bar.orientation;
            bar = new Pose(handle.position - (turn * offset), turn);
            handle.orientation = turn;
        }

        var lit = UI.LastElementFocused.IsActive() || UI.LastElementActive.IsActive();
        grab.Draw(Default.MaterialUI, handle.ToMatrix(), lit ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.45f, 0.45f, 0.45f));
    }

    /// <summary>A short tick on a controller as its laser comes onto a button.</summary>
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
