using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Mirador.Core.BaseStations;
using Microsoft.UI.Xaml.Controls;

namespace Mirador.App.Services;

public enum TrayState
{
    Stopped,
    Running,

    Problem,

    RunningProblem,
}

public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon icon;

    private readonly MenuFlyoutItem startStop;
    private readonly MenuFlyoutItem stationsOn;
    private readonly MenuFlyoutItem stationsOff;

    private readonly Dictionary<TrayState, System.Drawing.Icon> icons;
    private TrayState? shown;

    public TrayIcon(Action open, Action toggle, Func<Task> startOrStop, Func<StationCommand, Task> switchStations, Func<Task> quit)
    {
        icons = new Dictionary<TrayState, System.Drawing.Icon>
        {
            [TrayState.Stopped] = Load("TrayStopped.ico"),
            [TrayState.Running] = Load("TrayRunning.ico"),
            [TrayState.Problem] = Load("TrayProblem.ico"),
            [TrayState.RunningProblem] = Load("TrayRunningProblem.ico"),
        };

        startStop = new MenuFlyoutItem { Text = "Start", Command = new AsyncRelayCommand(startOrStop, AsyncRelayCommandOptions.AllowConcurrentExecutions) };
        stationsOn = new MenuFlyoutItem { Text = "Power on base stations", Command = new AsyncRelayCommand(() => switchStations(StationCommand.On)) };
        stationsOff = new MenuFlyoutItem { Text = "Power off base stations", Command = new AsyncRelayCommand(() => switchStations(StationCommand.Sleep)) };
        var menu = new MenuFlyout();
        menu.Items.Add(startStop);
        menu.Items.Add(stationsOn);
        menu.Items.Add(stationsOff);
        menu.Items.Add(new MenuFlyoutItem { Text = "Open", Command = new RelayCommand(open) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = new AsyncRelayCommand(quit) });

        icon = new TaskbarIcon
        {
            ToolTipText = "Mirador",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(toggle),
            NoLeftClickDelay = true,
        };
        Show(TrayState.Stopped, "Mirador: Monado is stopped", "Start");
        icon.ForceCreate(enablesEfficiencyMode: false);

        icon.TrayIcon.MessageWindow.SubscribeToKeyboardEventReceived((_, e) =>
        {
            if (e.KeyboardEvent == H.NotifyIcon.Core.KeyboardEvent.KeySelect)
            {
                toggle();
            }
        });
    }

    public void Show(TrayState state, string tooltip, string startStopText)
    {
        if (shown != state)
        {
            icon.UpdateIcon(icons[state]);
            shown = state;
        }

        icon.ToolTipText = tooltip;
        startStop.Text = startStopText;
    }

    public void ShowStations(bool enabled)
    {
        stationsOn.IsEnabled = enabled;
        stationsOff.IsEnabled = enabled;
    }

    public void Dispose()
    {
        icon.Dispose();
        foreach (var i in icons.Values)
        {
            i.Dispose();
        }
    }

    private static System.Drawing.Icon Load(string file)
    {
        const int SmallIconWidth = 49;
        var size = GetSystemMetricsForDpi(SmallIconWidth, GetDpiForSystem());
        return new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", file), size > 0 ? size : 16, size > 0 ? size : 16);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
