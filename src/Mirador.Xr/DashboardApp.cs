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

    /// <summary>At or under this charge a battery's bar is red.</summary>
    private const int LowPercent = 15;

    /// <summary>The red of a low battery and of the exit button's confirmation: a tint over the bar's own colour.</summary>
    private static readonly Color LowTint = new(3.2f, 0.5f, 0.4f);

    private readonly Link link;
    private readonly SystemButton button = new();
    private readonly Sprite power = Sprite.FromFile("power.png");
    private readonly Sprite recentre = Sprite.FromFile("recentre.png");
    private readonly Sprite confirm = Sprite.FromFile("confirm.png");
    private readonly Sprite charging = Sprite.FromFile("charging.png");
    private readonly Sprite desktop = Sprite.FromFile("desktop.png");
    private readonly TextStyle text = TextStyle.FromFont(Font.Default, TextM, Color.White);
    private readonly Dictionary<InteractorSource, IdHash> focused = [];

    private bool open;
    private Pose bar;
    private XrExit exits = XrExit.Monado;
    private IReadOnlyList<XrDevice> devices = [];

    // The desktop: the monitors as they were when the bar opened, and the one shown above the bar, if any.
    private IReadOnlyList<DesktopMonitor> monitors = [];
    private DesktopPanel? panel;
    private int shown = -1;
    private bool desktopFailed;

    // The origin asked for and not yet answered: where the stage goes, in the stage as it was.
    private MonadoPose? recentring;
    private double confirmUntil;

    public static int Run(Link link)
    {
        var settings = Program.Settings(XrLink.DashboardAppName);
        settings.overlayApp = true;
        settings.overlayPriority = uint.MaxValue; // above every other overlay
        settings.omitEmptyFrames = true; // closed, it submits no layer
        if (!SK.Initialize(settings))
        {
            return 1;
        }

        if (!SK.System.overlayApp)
        {
            // As a scene app it would take the view from every game.
            Console.Error.WriteLine("This OpenXR runtime has no overlay sessions.");
            SK.Shutdown();
            return 4;
        }

        var app = new DashboardApp(link);
        link.Send(XrLink.Running);
        SK.Run(app.Step);
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
        if (OperatingSystem.IsWindows())
        {
            DesktopMouse.UseRealPixels();
        }

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

        if (open)
        {
            DrawBar();
            panel?.Draw(bar);
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
            bar = ToPose(DashboardMaths.AheadOfHead(FromPose(Input.Head)));
            confirmUntil = 0;
            focused.Clear();
            monitors = OperatingSystem.IsWindows() && !desktopFailed ? DesktopMouse.Monitors() : [];
        }
        else if (!on)
        {
            ShowDesktop(-1);
        }

        open = on;
        Input.HandVisible(Handed.Max, on);
        Interaction.DefaultInteractors = on ? DefaultInteractors.Controllers : DefaultInteractors.None;
        Interaction.DefaultDraw = on;
    }

    private void DrawBar()
    {
        UI.PushTextStyle(text);
        var size = Vec2.One * UI.LineHeight;
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

            UI.PushId(i);
            UI.Label(device.Label);
            if (device.Percent is { } percent)
            {
                UI.SameLine();
                UI.PushTint(percent <= LowPercent ? LowTint : Color.White);
                UI.HProgressBar(percent / 100f, UI.LineHeight * 1.2f);
                UI.PopTint();
                UI.SameLine();
                UI.Label(percent.ToString(CultureInfo.CurrentCulture));
                if (device.Charging)
                {
                    UI.SameLine();
                    UI.Image(charging, size * 0.7f);
                }
            }

            UI.PopId();
        }

        UI.WindowEnd();
        UI.PopTextStyle();
    }

    /// <summary>Shows monitor <paramref name="index"/> above the bar, or (-1) none. A monitor Windows won't capture takes the desktop buttons away.</summary>
    private void ShowDesktop(int index)
    {
        panel?.Dispose();
        panel = null;
        shown = -1;
        if (index < 0 || index >= monitors.Count)
        {
            return;
        }

        try
        {
            panel = new DesktopPanel(monitors[index]);
            shown = index;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"The desktop can't be shown: {e.Message}");
            desktopFailed = true;
            monitors = [];
        }
    }

    /// <summary>Shows <paramref name="other"/> above the bar in a monitor's place; for trying the panel without a desktop.</summary>
    internal void ShowDesktop(DesktopPanel other)
    {
        panel?.Dispose();
        panel = other;
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
        if (recentring is not null || World.OriginMode != OriginMode.Stage)
        {
            return;
        }

        var head = FromPose(Input.Head);
        recentring = DashboardMaths.RecentredOrigin(head);
        link.Send(XrLink.FormatRecentre([head.Qx, head.Qy, head.Qz, head.Qw, head.X, head.Y, head.Z]));
        if (link.IsAlone)
        {
            recentring = null;
        }
    }

    /// <summary>Mirador moved the stage, or couldn't: the open bar stays where it is in the room, and both controllers tick.</summary>
    private void Recentred(bool moved)
    {
        if (recentring is { } origin && moved)
        {
            bar = ToPose(DashboardMaths.Compose(DashboardMaths.Inverse(origin), FromPose(bar)));
            Input.HapticPulse(InputHaptic.LController, 0, 0.6f, 0.1f);
            Input.HapticPulse(InputHaptic.RController, 0, 0.6f, 0.1f);
        }

        recentring = null;
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
