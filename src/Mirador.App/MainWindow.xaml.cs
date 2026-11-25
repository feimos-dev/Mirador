using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mirador.App.Pages;
using Mirador.App.Services;
using Mirador.Core;

namespace Mirador.App;

public sealed partial class MainWindow : Window
{
    private static readonly bool ShowsEyeTracking = false;

    private static readonly Dictionary<string, Type> PagesByTag = new()
    {
        ["Home"] = typeof(HomePage),
        ["EyeTracking"] = typeof(EyeTrackingPage),
        ["Lighthouse"] = typeof(LighthousePage),
        ["MonadoSettings"] = typeof(MonadoSettingsPage),
        ["Profiles"] = typeof(ProfilesPage),
        ["Diagnostics"] = typeof(DiagnosticsPage),
        ["Installed"] = typeof(InstalledPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private IEnumerable<NavigationViewItem> PaneItems =>
        NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Mirador.ico"));

        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Mirador.png")))
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 16,
            },
        };

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(handle) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(760 * scale)));
        minimumSize = new MinimumSize(handle, MinimumWidth, MinimumHeight);
        deviceChanges = new DeviceChanges(handle);
        deviceChanges.Changed += App.Current.Host.NotifyDevicesChanged;
        endSession = new EndSession(handle, App.Current.Host);
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnWindowChanged;
        Activated += OnActivated;
        if (!ShowsEyeTracking)
        {
            NavView.MenuItems.Remove(EyeTrackingItem);
        }

        NavView.SelectedItem = NavView.MenuItems[0];
        NavView.LayoutUpdated += FitPaneOnce;
        if (App.Current.Host.FirstRun)
        {
            _ = AskWhereMonadoComesFromAsync();
        }
    }

    private async Task AskWhereMonadoComesFromAsync()
    {
        var bundled = true;
        try
        {
            var root = await LoadedRootAsync();
            var choices = new RadioButtons { SelectedIndex = 0 };
            choices.Items.Add("Bundled Monado (recommended): upstream's weekly build, ready to run");
            choices.Items.Add("Build upstream main here (needs the build tools)");
            choices.Items.Add("Build a branch or merge request from its link");
            choices.Items.Add("A Monado I built myself (a build folder)");
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Where should Monado come from?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "Each profile can use another later, on the Profiles page.", TextWrapping = TextWrapping.Wrap },
                        choices,
                    },
                },
                PrimaryButtonText = "OK",
                DefaultButton = ContentDialogButton.Primary,
            };
            await dialog.ShowAsync();

            var host = App.Current.Host;
            bundled = choices.SelectedIndex == 0;
            var source = choices.SelectedIndex == 1 ? Core.Profiles.BuildSource.UpstreamMain : Core.Profiles.BuildSource.Mirador;
            var set = host.Profiles.Load();
            set = set with { Profiles = [.. set.Profiles.Select(p => p with { Monado = source })] };
            host.Profiles.Save(set);
            host.NotifyProfilesChanged();
            if (choices.SelectedIndex > 0)
            {
                Navigate("Profiles");
            }
        }
        catch (Exception ex)
        {
            Services.AppLog.Write("The first run's question failed", ex);
        }
        finally
        {
            App.Current.Host.AnswerFirstRun(bundled);
        }
    }

    private const int MinimumWidth = 760;

    private const int MinimumHeight = 500;

    private readonly MinimumSize minimumSize;

    private readonly DeviceChanges deviceChanges;

    private readonly EndSession endSession;

    public bool IsShown => AppWindow.IsVisible;

    public bool IsInForeground =>
        AppWindow.IsVisible && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);

    public Task<FrameworkElement> LoadedRootAsync()
    {
        var root = (FrameworkElement)Content;
        if (root.IsLoaded && root.XamlRoot is not null)
        {
            return Task.FromResult(root);
        }

        var loaded = new TaskCompletionSource<FrameworkElement>();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            loaded.TrySetResult(root);
        }

        root.Loaded += OnLoaded;
        return loaded.Task;
    }

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();

        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private long deactivatedAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 60;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            deactivatedAt = Stopwatch.GetTimestamp();
        }
        else
        {
            App.Current.HomeIfStarted?.OnWindowActivated();
        }
    }

    public void ToggleFromTray()
    {
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var since = Stopwatch.GetElapsedTime(deactivatedAt);
        if (TrayClick.ShouldHide(AppWindow.IsVisible, minimized, IsInForeground, since))
        {
            AppWindow.Hide();
        }
        else
        {
            ShowAndActivate();
        }
    }

    // Shown first and minimized only once its content has loaded: WinUI crashes when a window that was never laid
    // out is minimized.
    public async void ShowMinimized()
    {
        try
        {
            AppWindow.Show(activateWindow: false);
            await LoadedRootAsync();
            const int ShowMinNoActive = 7;
            ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), ShowMinNoActive);
        }
        catch (Exception ex)
        {
            Services.AppLog.Write("Couldn't start minimized", ex);
        }
    }

    private void FitPaneOnce(object? sender, object e)
    {
        if (FitPaneToLabels())
        {
            NavView.LayoutUpdated -= FitPaneOnce;
        }
    }

    private bool FitPaneToLabels()
    {
        if (!NavView.IsPaneOpen)
        {
            return false;
        }

        var widest = 0.0;
        foreach (var item in PaneItems)
        {
            if (item is not { Visibility: Visibility.Visible, Content: string text })
            {
                continue;
            }

            if (FindLabel(item, text) is not { } label || VisualTreeHelper.GetParent(label) is not UIElement holder)
            {
                return false;
            }

            var whole = new TextBlock
            {
                Text = text,
                FontFamily = label.FontFamily,
                FontSize = label.FontSize,
                FontWeight = label.FontWeight,
                FontStretch = label.FontStretch,
                CharacterSpacing = label.CharacterSpacing,
            };
            whole.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            var slot = LayoutInformation.GetLayoutSlot(label);
            var toPane = holder.TransformToVisual(NavView);
            var start = toPane.TransformPoint(new Windows.Foundation.Point(slot.Left, 0)).X;
            var roomEnd = toPane.TransformPoint(new Windows.Foundation.Point(slot.Right, 0)).X;
            var after = NavView.OpenPaneLength - roomEnd;
            widest = Math.Max(widest, start + whole.DesiredSize.Width + after);
        }

        if (widest <= 0)
        {
            return false;
        }

        NavView.OpenPaneLength = Math.Ceiling(widest);
        return true;
    }

    private static TextBlock? FindLabel(DependencyObject parent, string text)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock block && block.Text == text)
            {
                return block;
            }

            if (FindLabel(child, text) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    public void Navigate(string tag)
    {
        NavView.SelectedItem = PaneItems.FirstOrDefault(i => (string)i.Tag == tag);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Current.ClosingForGood)
        {
            return;
        }

        args.Cancel = true;
        if (App.Current.Quitting || App.Current.Host.CloseToTray())
        {
            sender.Hide();
        }
        else
        {
            _ = App.Current.QuitAsync();
        }
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
            && App.Current.Host.MinimizeToTray())
        {
            sender.Hide();
        }
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = PagesByTag[(string)((NavigationViewItem)args.SelectedItem).Tag];
        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
