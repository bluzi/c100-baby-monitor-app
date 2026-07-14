using System.Runtime.InteropServices;
using BabyMonitor.App.Services;
using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Shell;
using BabyMonitor.Core.Xiaomi;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App;

/// <summary>
/// The one window (WIN-14), the tray icon (WIN-1) and everything that hangs off them.
///
/// It decides nothing. Whether monitoring can be stopped, what the status says, whether the mini
/// window may fade — every one of those comes from the shared core, which is the same logic the phone
/// and the Mac run. This file is where those answers become pixels.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    private readonly AppState _state;
    private readonly TrayIcon _tray;
    private readonly MediaFoundationVideoRenderer _renderer = new();
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Updater _updater = new(Updater.CurrentVersion);
    private readonly IntPtr _hwnd;

    private SettingsWindow? _settings;

    /// <summary>
    /// The cameras on the account, for the tray's camera submenu (WIN-2). Held rather than fetched on
    /// demand: the device list is a *signed request to Xiaomi*, and the menu must be buildable the
    /// instant it is opened.
    /// </summary>
    private IReadOnlyList<CameraInfo> _cameras = Array.Empty<CameraInfo>();

    private bool _camerasRefreshing;
    private string _shape = DesktopShell.ShapeFull;
    private bool _pointerInside;
    private bool _chromePinned;
    private bool _chromeVisible = true;
    private bool _fullScreen;
    private bool _applyingShape;
    private bool _camerasLoaded;
    private bool _busy;
    private string? _nightVision;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _state = new AppState(DispatcherQueue);
        _state.StateChanged += UpdateUi;

        Title = "Baby Monitor";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "BabyMonitor.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // The picture: one surface, both shapes, never rebuilt (WIN-14).
        Video.SetMediaPlayer(_renderer.Player);
        _state.OnVideoRendererChanged(_renderer);

        // WIN-1/2: the tray icon is the app. It also owns the window that hears the machine sleep.
        _tray = new TrayIcon(BuildTrayMenu, ShowWindow);
        _tray.SystemSuspending += () => DispatcherQueue.TryEnqueue(_state.SystemWillSleep);
        _tray.SystemResumed += () => DispatcherQueue.TryEnqueue(() =>
        {
            _state.SystemDidWake();
            if (_state.SleepOutage != null)
            {
                ShowWindow(); // WIN-11: the outage is surfaced, not swallowed. Put it where it is seen.
            }
        });

        AppWindow.Closing += OnWindowClosing;
        AppWindow.Changed += OnWindowChanged;

        Root.PointerMoved += OnPointerMoved;
        Root.PointerExited += OnPointerExited;
        _chromeTimer.Tick += (_, _) => HideChrome();

        // ALRM-12: the trigger mark has to be placed against the bar's real width, which does not
        // exist until the window has laid itself out — and changes every time it is resized.
        LevelBar.SizeChanged += (_, _) => UpdateLevelBar();

        _ = LoadAppMarkAsync(); // UI-3: the same mark the phone and the Mac show

        RegionBox.ItemsSource = _state.Regions.Select(RegionName).ToList();
        RegionBox.SelectedIndex = _state.Regions.ToList().IndexOf("sg");
        AboutItem.Text = $"Baby Monitor {_state.Version}"; // LIVE-15 / UPD-6

        ApplyShape(Prefs.Shape, restoreFrame: true);
        UpdateUi();

        // BG-5: opening the window while monitoring shows the ongoing feed; opening it after a stop
        // starts monitoring again. It never restarts a running stream.
        if (_state.Screen == "viewer")
        {
            if (!_state.Running)
            {
                _state.Start();
            }

            _ = LoadNightVisionAsync(); // LIVE-10: show the mode the camera is actually in
            RefreshCameraList(); // so the camera submenu is populated the first time it is opened
        }

        if (_state.Screen == "login")
        {
            UsernameBox.Focus(FocusState.Programmatic); // AUTH-11: ready to type, no click first
        }

        StartUpdateChecks();
    }

    // --- the tray (WIN-1, WIN-2) ---------------------------------------------

    /// <summary>
    /// **WIN-2: the menu offers what the app can actually do right now, and nothing else.**
    ///
    /// There are three of them, because there are three genuinely different situations — and putting
    /// Mute and Show Camera in front of someone who has not signed in is the app describing a monitor
    /// that does not exist:
    ///
    ///  - **not signed in**: sign in. Nothing else is true yet.
    ///  - **signed in, no camera chosen**: choose one, or sign out.
    ///  - **watching**: everything, plus the account's cameras as a submenu with the watched one
    ///    checked, so a parent with two children can look at the other room from the tray instead of
    ///    walking back through the picker.
    ///
    /// It is built here, when the menu is opened — never on the state tick, which fires about twenty
    /// times a second while the feed is live.
    /// </summary>
    private IReadOnlyList<TrayItem> BuildTrayMenu()
    {
        RefreshCameraList(); // the menu is about to be read: the one moment its freshness matters

        var items = new List<TrayItem>();

        switch (_state.Screen)
        {
            case "login":
                // AUTH-1: nothing is being monitored and nothing can be. Say so, and offer the one
                // thing that changes it.
                items.Add(TrayItem.Label("Not signed in"));
                items.Add(TrayItem.Divider);
                items.Add(new TrayItem("Sign in…", () => Post(ShowWindow)));
                break;

            case "devices":
                // CAM-1: signed in, but no camera chosen — so there is still nothing to mute, and
                // nothing to show.
                items.Add(TrayItem.Label("No camera chosen"));
                items.Add(TrayItem.Divider);
                items.Add(new TrayItem("Choose camera…", () => Post(ShowWindow)));
                items.Add(new TrayItem("Sign out", () => Post(OnSignOutFromTray)));
                break;

            default:
                BuildMonitorMenu(items);
                break;
        }

        AppendAppItems(items);
        return items;
    }

    private void BuildMonitorMenu(List<TrayItem> items)
    {
        // WIN-2: what is happening, in words, before anything you can click.
        items.Add(TrayItem.Label(_state.StatusLine));

        if (_state.SleepOutage != null)
        {
            items.Add(new TrayItem($"⚠ {_state.SleepOutage}", () => Post(_state.DismissSleepOutage)));
        }

        if (_state.SleepUnprotected)
        {
            items.Add(TrayItem.Label("⚠ The PC may sleep and stop monitoring"));
        }

        items.Add(TrayItem.Divider);

        if (_state.Alarming)
        {
            items.Add(new TrayItem("Acknowledge alarm", () => Post(_state.Acknowledge)));
            items.Add(TrayItem.Divider);
        }

        // WIN-4 / LIVE-2: says which state it is IN, never what clicking would do.
        items.Add(new TrayItem(
            _state.Muted ? "Muted (sound off)" : "Sound on",
            () => Post(_state.ToggleMute),
            Checked: _state.Muted));

        items.Add(new TrayItem("Show camera", () => Post(ShowWindow)));
        items.Add(new TrayItem(
            "Mini window",
            () => Post(() =>
            {
                ShowWindow();
                ToggleShape();
            }),
            Checked: _shape == DesktopShell.ShapeMini));

        items.Add(CameraSubmenu());

        items.Add(TrayItem.Divider);

        // BG-11w / WATCH-11: there is no Stop on a PC — exiting is how a PC stops. Start is here only
        // for a monitor that failed on its own, and whether it appears is the core's decision
        // (DesktopShell.ViewerActions), tested there, so it cannot quietly drift from the window's.
        if (_state.CanResume)
        {
            items.Add(new TrayItem("Start monitoring", () => Post(_state.Start)));
        }

        items.Add(new TrayItem("Settings…", () => Post(ShowSettings)));
        items.Add(new TrayItem("Sign out", () => Post(OnSignOutFromTray))); // AUTH-10
    }

    /// <summary>
    /// CAM-4 / WIN-2: **the account's cameras, with the one being watched checked.**
    ///
    /// The list is whatever was last fetched, and it is refreshed when the menu opens — never on the
    /// state tick. Asking Xiaomi for the device list twenty times a second would be a signed request
    /// per tick, and an account that gets itself rate-limited is an account that cannot reconnect.
    /// </summary>
    private TrayItem CameraSubmenu()
    {
        var watched = _state.SelectedCamera?.Did;
        var children = new List<TrayItem>();

        if (_cameras.Count == 0)
        {
            children.Add(TrayItem.Label("Looking for cameras…"));
        }

        foreach (var camera in _cameras)
        {
            children.Add(new TrayItem(
                camera.Title,
                () => Post(() => _state.SwitchToCamera(camera)), // one click, and it is watching the other room
                Checked: camera.Did == watched));
        }

        children.Add(TrayItem.Divider);
        children.Add(new TrayItem("Choose camera…", () => Post(OnSwitchCameraFromTray)));
        return TrayItem.Submenu("Camera", children);
    }

    /// <summary>What is true of the app whatever it happens to be doing.</summary>
    private void AppendAppItems(List<TrayItem> items)
    {
        items.Add(TrayItem.Divider);

        if (_state.UpdateStatus.State == UpdateState.Failing)
        {
            // UPD-4: an app that has silently stopped updating looks exactly like one that is current.
            items.Add(TrayItem.Label($"⚠ Can't check for updates: {_state.UpdateStatus.Reason}"));
        }

        if (_state.UpdateStatus.State == UpdateState.Installed)
        {
            // UPD-7: it is on disk and it will run next time. Said once, quietly — the parent already
            // declined a restart, and an app that keeps asking is an app that gets ignored.
            items.Add(TrayItem.Label($"{_state.UpdateStatus.Version} installed — runs at the next launch"));
        }

        // The ellipsis means one thing: "this opens something, or goes and asks somebody". Neither
        // happens on the click itself, so it says so.
        items.Add(new TrayItem("Check for updates…", () => Post(() => _ = CheckForUpdateAsync(askedByUser: true))));

        items.Add(TrayItem.Divider);
        items.Add(TrayItem.Label($"Baby Monitor {_state.Version}"));

        // BG-11w / WIN-3: exiting IS stopping, so this is the control that ends the watch — and it
        // asks first while the monitor is running.
        items.Add(new TrayItem("Exit Baby Monitor", () => Post(() => _ = ConfirmExitAsync())));
    }

    /// <summary>
    /// The menu is about to be read, which is the one moment the camera list's freshness matters.
    ///
    /// This runs on the UI thread (the tray's window pumps its messages there), so it simply starts
    /// the fetch and lets the menu be built from what is already known. A menu cannot wait on Xiaomi:
    /// the shell would freeze under the pointer.
    /// </summary>
    private void RefreshCameraList()
    {
        if (_state.Screen != "viewer" || _camerasRefreshing)
        {
            return;
        }

        _camerasRefreshing = true;
        _ = RefreshCamerasAsync();
    }

    private async Task RefreshCamerasAsync()
    {
        try
        {
            // LoadCamerasAsync answers with a message rather than throwing (CAM-5) — a camera list the
            // tray could not refresh is not worth a word to the parent: the menu still works, and the
            // monitor never noticed.
            var (cameras, error) = await _state.LoadCamerasAsync();
            if (cameras != null)
            {
                _cameras = cameras;
            }
            else if (error != null)
            {
                Log.Debug("ui", $"could not refresh the tray's camera list: {error}");
            }
        }
        finally
        {
            _camerasRefreshing = false;
        }
    }

    private void OnSignOutFromTray()
    {
        _camerasLoaded = false;
        _cameras = Array.Empty<CameraInfo>();
        _state.SignOut();
        ApplyShape(DesktopShell.ShapeFull, restoreFrame: true);
        ShowWindow();
    }

    private void OnSwitchCameraFromTray()
    {
        _camerasLoaded = false;
        _state.SwitchCamera();
        ApplyShape(DesktopShell.ShapeFull, restoreFrame: true);
        ShowWindow();
    }

    /// <summary>
    /// WIN-1. **While the monitor is doing its job, this is just the app's mark.** No spinner, no
    /// grey, no second face for "reconnecting" — a tray icon that keeps changing is one a parent
    /// learns to stop reading.
    ///
    /// It changes for exactly two things, and both mean *go and look*: an alarm is ringing, or the
    /// monitor is not watching. A tray that looks the same whether the feed is live or dead would be
    /// the failure this whole project is built against, so those two are loud — and everything else,
    /// including the reconnect that fixes itself, is quiet.
    /// </summary>
    private void RefreshTray()
    {
        var icon = _state.Alarming ? "tray-alarm.ico" // ALRM-4: unmistakable, even with the speakers off
            : NotWatching() ? "tray-warning.ico" // an expired session, an unsupported camera, a failure
            : "tray-live.ico"; // the app's own mark: live, connecting, reconnecting — it is working

        _tray.Update(Path.Combine(AppContext.BaseDirectory, "Assets", icon), _state.StatusLine);
    }

    /// <summary>The monitor has stopped watching, and only a person can fix it.</summary>
    private bool NotWatching() =>
        !_state.Running ||
        _state.Status is Statuses.SessionExpired or Statuses.UnsupportedCamera or Statuses.MonitorFailed;

    /// <summary>
    /// **BG-11w / WIN-3: exiting is how a PC stops monitoring, so exiting asks first.**
    ///
    /// The phone protects its Stop button with a confirmation because a single stray tap must never
    /// end a watch. On a PC that weight has moved onto Exit — so the question lives here, on the one
    /// path out of the app, rather than on each of the buttons that happen to lead to it.
    ///
    /// It asks only while the monitor is actually running. Exiting an app that is not watching
    /// anything is just exiting an app.
    /// </summary>
    private async Task ConfirmExitAsync()
    {
        if (_state.Running)
        {
            ShowWindow(); // a question nobody can see is not a question
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Exit Baby Monitor?",
                Content = "Exiting stops monitoring: audio, the alarm and the connection all end. " +
                          "The baby will not be monitored until you open it again.",
                PrimaryButtonText = "Exit and stop monitoring",
                CloseButtonText = "Keep monitoring",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ExitApp();
    }

    /// <summary>WIN-9: Exit is the only thing that ends the app — and therefore the watch.</summary>
    private void ExitApp()
    {
        Log.Info("app", "exiting on the user's request — monitoring ends here");
        _state.Stop();
        _tray.Dispose();
        Application.Current.Exit();
    }

    // --- the window's two shapes (WIN-5, WIN-6, WIN-14) -----------------------

    private void ApplyShape(string preferred, bool restoreFrame = false)
    {
        // WIN-14: sign-in and the camera picker are never shown in a tile. The core decides that.
        var shape = DesktopShell.WindowShape(_state.Screen, preferred);
        if (!restoreFrame && shape == _shape)
        {
            return;
        }

        if (!restoreFrame)
        {
            RememberFrame(); // each shape keeps its own size and position
        }

        _shape = shape;
        Prefs.Shape = preferred;
        _applyingShape = true;

        var mini = shape == DesktopShell.ShapeMini;

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = mini; // WIN-5: it floats over other work
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: !mini);
                presenter.IsResizable = true;
                presenter.IsMaximizable = !mini;
                presenter.IsMinimizable = !mini;
            }

            // WIN-12 / WIN-22: the full window is in the taskbar and Alt-Tab like any other app. The
            // mini tile is not — it is already on top of everything, and listing it among the windows
            // a user is trying to see *past* would make it clutter twice over.
            AppWindow.IsShownInSwitchers = !mini;

            // The full shape is dragged by the strip under its caption buttons; the mini tile has no
            // caption at all, so it is dragged by its middle — and the strip is taken out of the way,
            // because a caption region swallows the input of anything drawn over it.
            TitleBarDrag.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
            SetTitleBar(mini ? MiniDragRegion : TitleBarDrag);

            var frame = Prefs.Frame(shape);
            if (frame is { } f)
            {
                AppWindow.MoveAndResize(new RectInt32(f.X, f.Y, f.Width, f.Height));
            }
            else if (shape == DesktopShell.ShapeMini)
            {
                AppWindow.Resize(new SizeInt32(360, 202));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(1100, 700));
            }
        }
        finally
        {
            _applyingShape = false;
        }

        UpdateUi();
    }

    private void ToggleShape() =>
        ApplyShape(_shape == DesktopShell.ShapeMini ? DesktopShell.ShapeFull : DesktopShell.ShapeMini);

    private void RememberFrame()
    {
        if (_fullScreen)
        {
            return; // full screen is not a size worth remembering
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        Prefs.SetFrame(_shape, position.X, position.Y, size.Width, size.Height);
    }

    /// <summary>
    /// WIN-19: the window takes the camera's shape, so the picture fills it edge to edge and is never
    /// framed in black bars. They were never the camera's bars — they were the window's.
    /// </summary>
    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _applyingShape || _fullScreen || _state.VideoAspect <= 0)
        {
            return;
        }

        if (_state.Screen != "viewer")
        {
            return; // a sign-in form does not take the camera's shape
        }

        var size = sender.Size;
        var chrome = _shape == DesktopShell.ShapeMini ? 0 : 32; // the title bar's own height
        var wanted = (int)Math.Round(((size.Width / _state.VideoAspect) + chrome));
        if (Math.Abs(wanted - size.Height) <= 2)
        {
            return;
        }

        _applyingShape = true;
        try
        {
            sender.Resize(new SizeInt32(size.Width, Math.Max(120, wanted)));
        }
        finally
        {
            _applyingShape = false;
        }
    }

    /// <summary>WIN-7 / WIN-9: closing the window closes the window. Monitoring does not notice.</summary>
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        HideWindow();
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        SetForegroundWindow(_hwnd);
        UpdateUi();

        // BG-5: reopening after a stop starts monitoring again; reopening while it runs never restarts it.
        if (_state.Screen == "viewer" && !_state.Running)
        {
            _state.Start();
        }
    }

    private void HideWindow()
    {
        RememberFrame();
        AppWindow.Hide();
        PowerRequests.HoldDisplay(false); // LIVE-14: a monitor nobody is looking at holds no screen on
        Log.Info("ui", "the window was closed — monitoring carries on in the tray");
    }

    // --- the pointer (LIVE-11w, WIN-15/16) -----------------------------------

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        _chromeVisible = true;
        _chromeTimer.Stop();
        _chromeTimer.Start();
        UpdateChrome();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        _chromeTimer.Stop();
        _chromeTimer.Start();
        UpdateChrome();
    }

    /// <summary>Nothing may vanish from under the pointer that is reaching for it.</summary>
    private void OnChromePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _chromePinned = true;
        _chromeTimer.Stop();
        _chromeVisible = true;
        UpdateChrome();
    }

    private void OnChromePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _chromePinned = false;
        _chromeTimer.Start();
    }

    private void HideChrome()
    {
        _chromeTimer.Stop();
        if (_chromePinned)
        {
            return;
        }

        _chromeVisible = false;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var visible = _chromeVisible || _chromePinned;
        ControlBar.Opacity = visible ? 1 : 0;
        ControlBar.IsHitTestVisible = visible;

        // WIN-15: close, make-it-full and acknowledge come and go with the pointer. Mute does not —
        // it is what tells a tile too small for words that the sound is off (WIN-5 / LIVE-2).
        MiniTopControls.Opacity = _pointerInside ? 1 : 0;
        MiniTopControls.IsHitTestVisible = _pointerInside;

        // WIN-16: how solid the tile is drawn is the core's decision, not this file's.
        SetWindowOpacity(_shape == DesktopShell.ShapeMini ? _state.MiniOpacity(_pointerInside) : 1.0);
    }

    /// <summary>WIN-16: the mini window fades — through a layered window, which is how Windows does it.</summary>
    private void SetWindowOpacity(double opacity)
    {
        try
        {
            var style = GetWindowLong(_hwnd, GwlExStyle);
            if (opacity >= 1.0)
            {
                if ((style & WsExLayered) != 0)
                {
                    SetWindowLong(_hwnd, GwlExStyle, style & ~WsExLayered);
                }

                return;
            }

            if ((style & WsExLayered) == 0)
            {
                SetWindowLong(_hwnd, GwlExStyle, style | WsExLayered);
            }

            SetLayeredWindowAttributes(_hwnd, 0, (byte)(Math.Clamp(opacity, 0.05, 1.0) * 255), LwaAlpha);
        }
        catch (Exception e)
        {
            Log.Warn("ui", $"could not set the window's opacity: {e.Message}");
        }
    }

    // --- keyboard (WIN-13) ----------------------------------------------------

    private void OnToggleShapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleShape();
        args.Handled = true;
    }

    private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullScreen(!_fullScreen);
        args.Handled = true;
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_fullScreen)
        {
            return; // Esc means "leave full screen", and nothing else — it never closes the monitor
        }

        SetFullScreen(false);
        args.Handled = true;
    }

    private void SetFullScreen(bool wanted)
    {
        if (_state.Screen != "viewer" || _shape == DesktopShell.ShapeMini)
        {
            return;
        }

        _fullScreen = wanted;
        AppWindow.SetPresenter(wanted ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    // --- everything the UI shows ---------------------------------------------

    private void UpdateUi()
    {
        var screen = _state.Screen;
        var viewer = screen == "viewer";
        var mini = viewer && _shape == DesktopShell.ShapeMini;

        LoginPanel.Visibility = screen == "login" ? Visibility.Visible : Visibility.Collapsed;
        CamerasPanel.Visibility = screen == "devices" ? Visibility.Visible : Visibility.Collapsed;
        ViewerChrome.Visibility = viewer && !mini ? Visibility.Visible : Visibility.Collapsed;
        MiniChrome.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        Video.Visibility = viewer ? Visibility.Visible : Visibility.Collapsed;

        if (screen == "devices" && !_camerasLoaded)
        {
            _camerasLoaded = true;
            _ = LoadCamerasAsync();
        }

        // BG-8: an expired session sends the user back to sign-in rather than looping "connection lost".
        if (_state.SessionExpired && screen == "login")
        {
            ShowLoginError("Your session expired — please sign in again.");
        }

        var color = StatusColor();
        StatusDot.Fill = new SolidColorBrush(color);
        MiniStatusDot.Fill = new SolidColorBrush(color);
        StatusLineText.Text = _state.StatusLine;
        MiniStatusText.Text = _state.Muted ? $"{_state.StatusText} · muted" : _state.StatusText;
        LevelText.Text = _state.Running ? $"{(int)_state.Level} dB" : string.Empty;

        UpdateLevelBar();

        MuteIcon.Glyph = _state.Muted ? "\uE74F" : "\uE767"; // Segoe Fluent: Mute / Volume
        MiniMuteIcon.Glyph = MuteIcon.Glyph;

        // LIVE-2: muted draws latched — a filled, attention-coloured well, so it can never be misread
        // as "press to mute".
        var latched = new SolidColorBrush(Color.FromArgb(0xE6, 0xC6, 0x28, 0x28));
        MuteButton.Background = _state.Muted ? latched : new SolidColorBrush(Colors.Transparent);
        MiniMute.Background = _state.Muted
            ? latched
            : new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        ToolTipService.SetToolTip(
            MuteButton,
            _state.Muted ? "Muted — the alarm still works. Click for sound" : "Mute the speaker");

        // BG-11w: Start appears only for a monitor that stopped working. There is no Stop to sit
        // beside it — so its separator goes with it rather than floating at the head of the row.
        StartButton.Visibility = _state.CanResume ? Visibility.Visible : Visibility.Collapsed;
        StartSeparator.Visibility = StartButton.Visibility;

        AlarmBanner.Visibility = _state.Alarming ? Visibility.Visible : Visibility.Collapsed;
        AlarmText.Text = _state.AlarmText;
        MiniAcknowledge.Visibility = _state.Alarming ? Visibility.Visible : Visibility.Collapsed;
        MiniAlarmBorder.BorderThickness = new Thickness(_state.Alarming ? 2 : 0);

        OutageBanner.Visibility = _state.SleepOutage != null ? Visibility.Visible : Visibility.Collapsed;
        OutageText.Text = _state.SleepOutage ?? string.Empty;

        FeedbackBanner.Visibility = _state.AskingCryFeedback ? Visibility.Visible : Visibility.Collapsed;
        StartupBanner.Visibility = _state.StartupOfferPending && viewer
            ? Visibility.Visible
            : Visibility.Collapsed;

        SleepWarning.Visibility = _state.SleepUnprotected ? Visibility.Visible : Visibility.Collapsed;
        NetworkWarning.Visibility = _state.NetworkDown ? Visibility.Visible : Visibility.Collapsed;
        VideoWarning.Visibility = _state.VideoUnavailable ? Visibility.Visible : Visibility.Collapsed;

        // LIVE-14: the display stays awake while a window is showing a live feed — and only then.
        PowerRequests.HoldDisplay(AppWindow.IsVisible && _state.Status == Statuses.Live);

        UpdateChrome();
        RefreshTray();
    }

    private void UpdateLevelBar()
    {
        var width = LevelBar.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(_state.Level / Math.Max(_state.LevelMax, 1), 0, 1);
        var past = _state.AlarmEnabled && _state.Level >= _state.ThresholdDb;
        LevelFill.Width = width * fraction;
        LevelFill.Fill = new SolidColorBrush(past
            ? Color.FromArgb(0xFF, 0xE5, 0x53, 0x3B)
            : Color.FromArgb(0xFF, 0x4C, 0xD9, 0x64));

        // ALRM-12: where it will ring. The one number on this screen that decides whether a parent is
        // woken — including any learned adjustment (ALRM-16).
        LevelMark.Visibility = _state.AlarmEnabled ? Visibility.Visible : Visibility.Collapsed;
        var mark = Math.Clamp(_state.ThresholdDb / Math.Max(_state.LevelMax, 1), 0, 1);
        LevelMark.Margin = new Thickness((width * mark) - 1, 0, 0, 0);
    }

    private Color StatusColor()
    {
        if (_state.Alarming)
        {
            return Color.FromArgb(0xFF, 0xE5, 0x39, 0x35);
        }

        if (!_state.Running)
        {
            return Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E);
        }

        return _state.Status switch
        {
            Statuses.Live => Color.FromArgb(0xFF, 0x4C, 0xD9, 0x64),
            Statuses.SessionExpired or Statuses.UnsupportedCamera or Statuses.MonitorFailed =>
                Color.FromArgb(0xFF, 0xFF, 0xB7, 0x4D),
            _ => Color.FromArgb(0xFF, 0xFF, 0xD5, 0x4F),
        };
    }

    // --- the controls ---------------------------------------------------------

    /// <summary>
    /// BG-11w: Start, and no Stop. It is here only for a monitor that failed on its own (WATCH-11) —
    /// which must be recoverable right where the parent is looking, not by exiting the app.
    /// </summary>
    private void OnStart(object sender, RoutedEventArgs e) => _state.Start();

    private void OnCheckForUpdates(object sender, RoutedEventArgs e) =>
        _ = CheckForUpdateAsync(askedByUser: true); // UPD-9

    private void OnExit(object sender, RoutedEventArgs e) => _ = ConfirmExitAsync();

    private void OnToggleMute(object sender, RoutedEventArgs e) => _state.ToggleMute();

    private void OnAcknowledge(object sender, RoutedEventArgs e) => _state.Acknowledge();

    private void OnCryYes(object sender, RoutedEventArgs e) => _state.SubmitCryFeedback(true);

    private void OnCryNo(object sender, RoutedEventArgs e) => _state.SubmitCryFeedback(false);

    private void OnCryDismiss(object sender, RoutedEventArgs e) => _state.DismissCryFeedback();

    private void OnDismissOutage(object sender, RoutedEventArgs e) => _state.DismissSleepOutage();

    private void OnEnableStartup(object sender, RoutedEventArgs e) => _state.SetStartWithWindows(true);

    private void OnDeclineStartup(object sender, RoutedEventArgs e) => _state.AnswerStartupOffer();

    private void OnMakeMini(object sender, RoutedEventArgs e) => ApplyShape(DesktopShell.ShapeMini);

    private void OnMakeFull(object sender, RoutedEventArgs e) => ApplyShape(DesktopShell.ShapeFull);

    private void OnHideWindow(object sender, RoutedEventArgs e) => HideWindow();

    private void OnSwitchCamera(object sender, RoutedEventArgs e)
    {
        _camerasLoaded = false;
        _state.SwitchCamera();
        ApplyShape(DesktopShell.ShapeFull, restoreFrame: true);
    }

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        _camerasLoaded = false;
        _state.SignOut();
        ApplyShape(DesktopShell.ShapeFull, restoreFrame: true);
    }

    private void OnShowSettings(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_state);
            _settings.Closed += (_, _) => _settings = null;
        }

        _settings.Activate();
    }

    private void OnOpenNetworkSettings(object sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri("ms-settings:network"));

    /// <summary>WIN-20: the shortest route to a picture — the free extension, in the Store.</summary>
    private void OnInstallHevc(object sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri("ms-windows-store://pdp/?ProductId=9n4wgh0z6vhq"));

    // --- night vision (LIVE-10) ----------------------------------------------

    private async void OnNightVision(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item)
        {
            return;
        }

        var mode = item.Name switch
        {
            "NightOn" => NightVisionMode.On,
            "NightOff" => NightVisionMode.Off,
            _ => NightVisionMode.Auto,
        };

        var error = await _state.SetNightVisionAsync(mode);
        if (error != null)
        {
            // LIVE-10: a readable error, and the displayed mode does not change.
            ShowNightVisionError(error);
            ShowNightVision(_nightVision);
            return;
        }

        _nightVision = mode.ToString();
        ShowNightVisionError(null);
    }

    private async Task LoadNightVisionAsync()
    {
        var (mode, error) = await _state.GetNightVisionAsync();
        _nightVision = mode?.ToString();
        ShowNightVision(_nightVision);
        ShowNightVisionError(error);
    }

    private void ShowNightVision(string? mode)
    {
        NightOn.IsChecked = mode == nameof(NightVisionMode.On);
        NightOff.IsChecked = mode == nameof(NightVisionMode.Off);
        NightAuto.IsChecked = mode == nameof(NightVisionMode.Auto);
    }

    private void ShowNightVisionError(string? error)
    {
        NightError.Text = error ?? string.Empty;
        NightError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        NightErrorSeparator.Visibility = NightError.Visibility;
    }

    // --- sign in (AUTH-1/3/4/9/11) -------------------------------------------

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return; // a second click while Xiaomi thinks would sign in twice
        }

        SetBusy(true);
        ShowLoginError(null);

        try
        {
            SignInStep step;
            if (CodeGroup.Visibility == Visibility.Visible)
            {
                step = await _state.SubmitCodeAsync(CodeBox.Text.Trim());
            }
            else if (CaptchaGroup.Visibility == Visibility.Visible)
            {
                step = await _state.SubmitCaptchaAsync(CaptchaBox.Text.Trim());
            }
            else
            {
                var region = _state.Regions[Math.Max(0, RegionBox.SelectedIndex)];
                step = await _state.SignInAsync(UsernameBox.Text.Trim(), PasswordField.Password, region);
            }

            await HandleSignInStepAsync(step);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task HandleSignInStepAsync(SignInStep step)
    {
        switch (step.Kind)
        {
            case "ok":
                ResetLogin();
                _camerasLoaded = false;
                UpdateUi();
                break;

            case "captcha":
                // AUTH-3: a rejected code yields a fresh captcha to retry, not a failure.
                CredentialsGroup.Visibility = Visibility.Collapsed;
                CodeGroup.Visibility = Visibility.Collapsed;
                CaptchaGroup.Visibility = Visibility.Visible;
                StartOverLink.Visibility = Visibility.Visible;
                LoginSubtitle.Text = "Xiaomi wants to check you are human";
                SignInButton.Content = "Continue";
                CaptchaBox.Text = string.Empty;
                if (step.CaptchaImage != null)
                {
                    CaptchaImage.Source = await LoadImageAsync(step.CaptchaImage);
                }

                CaptchaBox.Focus(FocusState.Programmatic);
                break;

            case "code":
                // AUTH-4/11: the code field is focused with the cursor in it — typing just works.
                CredentialsGroup.Visibility = Visibility.Collapsed;
                CaptchaGroup.Visibility = Visibility.Collapsed;
                CodeGroup.Visibility = Visibility.Visible;
                StartOverLink.Visibility = Visibility.Visible;
                LoginSubtitle.Text = $"A verification code was sent to your {step.Channel} {step.MaskedTarget}";
                SignInButton.Content = "Verify";
                CodeBox.Text = string.Empty;
                CodeBox.Focus(FocusState.Programmatic);
                break;

            default:
                ShowLoginError(step.Message ?? "Sign-in failed.");
                break;
        }
    }

    private void OnStartOver(object sender, RoutedEventArgs e) => ResetLogin();

    private void ResetLogin()
    {
        CredentialsGroup.Visibility = Visibility.Visible;
        CaptchaGroup.Visibility = Visibility.Collapsed;
        CodeGroup.Visibility = Visibility.Collapsed;
        StartOverLink.Visibility = Visibility.Collapsed;
        LoginSubtitle.Text = "Sign in to the Mi account that owns the camera";
        SignInButton.Content = "Sign in";
        CaptchaBox.Text = string.Empty;
        CodeBox.Text = string.Empty;
        ShowLoginError(null);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        LoginBusy.IsActive = busy;
        SignInButton.IsEnabled = !busy;
    }

    private void ShowLoginError(string? message)
    {
        LoginError.Message = message ?? string.Empty;
        LoginError.IsOpen = message != null;
    }

    /// <summary>
    /// The app's mark, read off disk as bytes rather than pointed at with a URI. `BitmapImage.UriSource`
    /// speaks ms-appx / ms-appdata / http — a `file:` URI fails, and fails *silently*, leaving a blank
    /// square where the app's face should be.
    /// </summary>
    private async Task LoadAppMarkAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "BabyMonitor.png");
            AppMark.Source = await LoadImageAsync(await File.ReadAllBytesAsync(path));
        }
        catch (Exception e)
        {
            Log.Warn("ui", $"could not load the app mark: {e.Message}");
        }
    }

    private static async Task<BitmapImage> LoadImageAsync(byte[] bytes)
    {
        var image = new BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(bytes));
        stream.Seek(0);
        await image.SetSourceAsync(stream);
        return image;
    }

    // --- cameras (CAM-1/5) ----------------------------------------------------

    private void OnReloadCameras(object sender, RoutedEventArgs e) => _ = LoadCamerasAsync();

    private async Task LoadCamerasAsync()
    {
        CamerasBusy.IsActive = true;
        CamerasError.IsOpen = false;
        CamerasList.ItemsSource = null;

        var (cameras, error) = await _state.LoadCamerasAsync();

        CamerasBusy.IsActive = false;
        if (error != null)
        {
            CamerasError.Message = error;
            CamerasError.IsOpen = true;
            return;
        }

        if (cameras == null || cameras.Count == 0)
        {
            // An account with no cameras says so rather than showing an empty screen (CAM-5).
            CamerasError.Message = "This Mi account has no cameras on it.";
            CamerasError.IsOpen = true;
            return;
        }

        CamerasList.ItemsSource = cameras;
        CamerasList.DisplayMemberPath = nameof(CameraInfo.Title);
    }

    private void OnCameraChosen(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CameraInfo camera)
        {
            return;
        }

        _state.SelectCamera(camera);
        _state.Start(); // CAM-2: choosing a camera opens its live feed
        UpdateUi();
        _ = LoadNightVisionAsync();
    }

    // --- updates (UPD-3/4/5/7) ------------------------------------------------

    /// <summary>
    /// **UPD-3: the app checks for an update at launch, and never again while it runs.**
    ///
    /// An updater that checked every few hours could find one at 3am — and an update nobody asked for,
    /// arriving in the middle of the night, is a risk with no upside whatsoever. A monitor has nothing
    /// to gain from learning about a new version before morning. So: once, at launch, when the parent
    /// is demonstrably at the machine, because they just opened it. After that, only the checks a
    /// human asks for (UPD-9).
    /// </summary>
    private void StartUpdateChecks()
    {
        // UPD-5w: an update a previous run put in place is applied HERE, before monitoring starts —
        // the one moment the swap costs nothing, because nothing is being watched yet. (Windows will
        // not let a running program overwrite its own files, so this is where "it takes over at the
        // next launch" actually happens.)
        var staged = Updater.FindStaged(Updater.CurrentVersion);
        if (staged != null && Updater.Install(staged))
        {
            Application.Current.Exit();
            return;
        }

        // Nothing newer is waiting, so whatever is left beside us is this version or older: the update
        // landed, and the folder it came out of is just disk now.
        Updater.CleanStaging(Updater.CurrentVersion);

        _ = CheckForUpdateAsync(askedByUser: false);
    }

    private async Task CheckForUpdateAsync(bool askedByUser)
    {
        _state.UpdateStatus = new UpdateStatus(UpdateState.Checking);
        try
        {
            var version = await _updater.CheckAsync();
            if (version == null)
            {
                _state.UpdateStatus = UpdateStatus.Idle;
                if (askedByUser)
                {
                    await TellUserAsync("Baby Monitor is up to date.", $"You are running {_state.Version}.");
                }

                return;
            }

            await OfferRestartAsync(version);
        }
        catch (NoTokenException)
        {
            // Not a failure — updates simply have not been set up yet, and settings say so. An updater
            // that cried wolf on a fresh install is one nobody reads by the time it matters.
            _state.UpdateStatus = UpdateStatus.Idle;
            if (askedByUser)
            {
                await TellUserAsync(
                    "Updates are not set up.",
                    "Baby Monitor needs a GitHub token to read its private repository. Add one in Settings.");
            }
        }
        catch (Exception e)
        {
            // UPD-8: a failed check never touches monitoring. UPD-4: nor is it swallowed.
            Log.Warn("update", $"check failed: {e.Message}");
            _state.UpdateStatus = new UpdateStatus(UpdateState.Failing, Reason: e.Message);
            if (askedByUser)
            {
                await TellUserAsync("Could not check for updates.", e.Message);
            }
        }
    }

    /// <summary>
    /// **UPD-5.** The new version is on disk; the running monitor was never touched and is still
    /// watching. Then — and only then — the app asks, once, whether to restart into it.
    ///
    /// Saying no is a real answer: nothing happens, nothing is asked again, and the version already on
    /// disk takes over at the next launch. The app never restarts itself, which is the promise this
    /// updater exists to keep.
    /// </summary>
    private async Task OfferRestartAsync(string version)
    {
        _state.UpdateStatus = new UpdateStatus(UpdateState.Installed, version);
        ShowWindow(); // the question is asked where it can be seen, and answered

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Baby Monitor {version} is installed.",
            Content = _state.Running
                ? "It will run the next time you open Baby Monitor. Restarting now takes a few " +
                  "seconds, during which the baby is not monitored — monitoring resumes by itself " +
                  "afterwards."
                : "It will run the next time you open Baby Monitor.",
            PrimaryButtonText = "Restart now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            Log.Info("update", $"the user chose not to restart — {version} will run at the next launch");
            return;
        }

        if (_updater.Staged != null && Updater.Install(_updater.Staged))
        {
            _state.Stop(); // they asked for this restart; the watch resumes on the other side
            Application.Current.Exit();
        }
    }

    private async Task TellUserAsync(string title, string body)
    {
        ShowWindow();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = body,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    // --- odds and ends --------------------------------------------------------

    private void Post(Action action) => DispatcherQueue.TryEnqueue(() => action());

    /// <summary>Xiaomi's server codes mean nothing to anyone; picking the wrong one looks exactly like a
    /// wrong password. So name them.</summary>
    private static string RegionName(string code) => code switch
    {
        "cn" => "China",
        "de" => "Europe",
        "us" => "United States",
        "ru" => "Russia",
        "sg" => "Singapore",
        "i2" => "India",
        _ => code.ToUpperInvariant(),
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
