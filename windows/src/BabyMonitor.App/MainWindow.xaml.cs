using System.Diagnostics;
using System.Runtime.InteropServices;
using BabyMonitor.App.Services;
using BabyMonitor.Core.Data;
using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Shell;
using BabyMonitor.Core.Ui;
using BabyMonitor.Core.Xiaomi;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Log = BabyMonitor.App.Services.Logging.Log;
// This file needs Microsoft.UI.Xaml.Shapes (Rectangle, for the level bar), which also defines a
// `Path` type — so bare `Path` collides with System.IO.Path (CS0104). We only ever mean the latter.
using Path = System.IO.Path;

namespace BabyMonitor.App;

/// <summary>
/// The one window (DESK-9), the tray icon (DESK-1) and everything that hangs off them.
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
    private readonly SoftwareHevcVideoRenderer _renderer;
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Updater _updater = new(Updater.CurrentVersion);

    /// <summary>Only one ContentDialog may be open at a time — see <see cref="AskAsync"/>.</summary>
    private readonly SemaphoreSlim _dialogLock = new(1, 1);
    private readonly IntPtr _hwnd;

    private SettingsWindow? _settings;

    /// <summary>
    /// The cameras on the account, for the tray's camera submenu (DESK-2). Held rather than fetched on
    /// demand: the device list is a *signed request to Xiaomi*, and the menu must be buildable the
    /// instant it is opened.
    /// </summary>
    private IReadOnlyList<CameraInfo> _cameras = Array.Empty<CameraInfo>();

    private bool _camerasRefreshing;
    private string _shape = DesktopShell.ShapeFull;
    private string _appliedMiniCorner = Prefs.MiniCorner;
    private string _lastScreen = string.Empty;
    private bool _pointerInside;
    private bool _chromePinned;
    private bool _chromeVisible = true;
    private bool _fullScreen;
    private bool _applyingShape;
    private bool _camerasLoaded;
    private bool _busy;
    private string? _nightVision;
    private bool _exiting;

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

        // The picture: one surface, both shapes, never rebuilt (DESK-9). The decoder hands its bitmap
        // over when it has one; until then there is simply nothing to draw.
        _renderer = new SoftwareHevcVideoRenderer(DispatcherQueue, bitmap => Video.Source = bitmap);
        _state.OnVideoRendererChanged(_renderer);

        // DESK-1/2: the tray icon is the app. It also owns the window that hears the machine sleep.
        _tray = new TrayIcon(BuildTrayMenu, ShowWindow);
        _tray.SystemSuspending += () => DispatcherQueue.TryEnqueue(_state.SystemWillSleep);
        _tray.SystemResumed += () => DispatcherQueue.TryEnqueue(() =>
        {
            _state.SystemDidWake();
            if (_state.SleepOutage != null)
            {
                // DESK-21: the outage is surfaced, not swallowed. Bring the window forward — in whatever
                // shape the parent left it (forcing full here would silently erase a mini preference).
                // The full view shows the whole sentence; the tile shows a short form in its status line
                // and stays opaque because an unread outage counts as needing attention (DESK-11).
                ShowWindow();
            }
        });

        // A second launch (Start-menu shortcut while we sit in the tray) does not start a second
        // monitor — it asks us to come forward. The listener fires off the UI thread, so hop onto it.
        SingleInstance.OnActivated(() => DispatcherQueue.TryEnqueue(ShowWindow));

        AppWindow.Closing += OnWindowClosing;
        AppWindow.Changed += OnWindowChanged;

        Root.PointerMoved += OnPointerMoved;
        Root.PointerExited += OnPointerExited;
        _chromeTimer.Tick += (_, _) => HideChrome();

        // ALRM-12: the trigger mark has to be placed against the bar's real width, which does not
        // exist until the window has laid itself out — and changes every time it is resized.
        LevelBar.SizeChanged += (_, _) => UpdateLevelBar();
        MiniLevelBar.SizeChanged += (_, _) => UpdateLevelBar();

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

        StartUpdateChecks();
    }

    // --- the tray (DESK-1, DESK-2) ---------------------------------------------

    /// <summary>
    /// **DESK-2: the menu offers what the app can actually do right now, and nothing else.**
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
        // DESK-2: what is happening, in words, before anything you can click.
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

        // DESK-4 / LIVE-2: says which state it is IN, never what clicking would do.
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

        // BG-14 / WATCH-11: there is no Stop on a PC — exiting is how a PC stops. Start is here only
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
    /// CAM-4 / DESK-2: **the account's cameras, with the one being watched checked.**
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

        // BG-14 / DESK-3: exiting IS stopping, so this is the control that ends the watch — and it
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
        ApplyShape(Prefs.Shape, restoreFrame: true); // display full, keep the user's shape (DESK-9)
        ShowWindow();
    }

    private void OnSwitchCameraFromTray()
    {
        _camerasLoaded = false;
        _state.SwitchCamera();
        ApplyShape(Prefs.Shape, restoreFrame: true); // display full, keep the user's shape (DESK-9)
        ShowWindow();
    }

    /// <summary>
    /// DESK-1. **While the monitor is doing its job, this is just the app's mark.** No spinner, no
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
    /// **BG-14 / DESK-3: exiting is how a PC stops monitoring, so exiting asks first.**
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
            var answer = await AskAsync(new ContentDialog
            {
                Title = "Exit Baby Monitor?",
                Content = "Exiting stops monitoring: audio, the alarm and the connection all end. " +
                          "The baby will not be monitored until you open it again.",
                PrimaryButtonText = "Exit and stop monitoring",
                CloseButtonText = "Keep monitoring",
                DefaultButton = ContentDialogButton.Close,
            });

            if (answer != ContentDialogResult.Primary)
            {
                return; // including a question that could not be asked: never end a watch by default
            }
        }

        ExitApp();
    }

    /// <summary>DESK-6: Exit is the only thing that ends the app — and therefore the watch.</summary>
    private void ExitApp()
    {
        Log.Info("app", "exiting on the user's request — monitoring ends here");
        _state.Stop();
        Shutdown();
    }

    /// <summary>
    /// Every path that actually ends the process goes through here — user Exit, and the "Restart now"
    /// hand-off to an update. It sets <see cref="_exiting"/> *before* asking the app to exit, because
    /// `Application.Exit()` closes the window through <see cref="OnWindowClosing"/>, which otherwise
    /// cancels the close and leaves the app running: Exit that does not exit, and an update restart
    /// that leaves the old process alive beside the new one. It also removes the tray icon by hand —
    /// the process dies too fast for Windows to reap it, so without this a dead icon lingers, showing
    /// the last state it had, until the user waves the mouse over it (a tray icon that lies — DESK-1).
    /// </summary>
    private void Shutdown()
    {
        _exiting = true;
        RememberFrame(); // DESK-9: keep where each shape was, even when the way out is Exit
        _tray.Dispose();
        Application.Current.Exit();

        // A hard backstop. In an unpackaged WinUI 3 app `Application.Exit()` does not always fully
        // terminate the process; if it hangs, the leftover holds the single-instance mutex, so every
        // future launch would see "already running" and show nothing — the monitor would be
        // unstartable until a reboot. A short-delay Environment.Exit guarantees the process actually
        // goes. On a clean exit the process is gone long before this fires.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Log.Warn("app", "the app did not exit on its own — forcing it");
            Environment.Exit(0);
        });
    }

    // --- the window's two shapes (DESK-8, DESK-7, DESK-9) -----------------------

    /// <summary>
    /// Give the window a shape. <paramref name="preferred"/> is the shape the user would keep; the
    /// shape actually shown is what the current screen allows (DESK-5: sign-in and the picker are never
    /// a tile). <paramref name="persist"/> is what separates a *choice* from a *reshape*: only the user
    /// toggling shape writes the preference — a screen forcing full (sign-out, an expiry) must not
    /// quietly rewrite the parent's mini choice, or the floating tile they set up overnight is gone for
    /// good the first time they sign out (DESK-8, DESK-9).
    /// </summary>
    private void ApplyShape(string preferred, bool restoreFrame = false, bool persist = false)
    {
        var shape = DesktopShell.WindowShape(_state.Screen, preferred);
        if (!restoreFrame && shape == _shape)
        {
            if (persist)
            {
                Prefs.Shape = preferred; // the choice still counts even if the window need not move
            }

            return;
        }

        if (!restoreFrame)
        {
            RememberFrame(); // each shape keeps its own size and position
        }

        _shape = shape;
        if (persist)
        {
            Prefs.Shape = preferred;
        }

        _applyingShape = true;

        var mini = shape == DesktopShell.ShapeMini;

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = mini; // DESK-8: it floats over other work
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: !mini);
                presenter.IsResizable = true;
                presenter.IsMaximizable = !mini;
                presenter.IsMinimizable = !mini;
            }

            // DESK-14 / DESK-15: the full window is in the taskbar and Alt-Tab like any other app. The
            // mini tile is not — it is already on top of everything, and listing it among the windows
            // a user is trying to see *past* would make it clutter twice over.
            AppWindow.IsShownInSwitchers = !mini;

            // The full shape is dragged by the strip under its caption buttons; the mini tile has no
            // caption at all, so it is dragged by its middle — and the strip is taken out of the way,
            // because a caption region swallows the input of anything drawn over it.
            TitleBarDrag.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
            SetTitleBar(mini ? MiniDragRegion : TitleBarDrag);

            // DESK-25: a remembered frame is only given back if it would land where the parent can see it.
            // A frame remembered from a minimised window, or from a display that has since gone away,
            // is discarded for the shape's default — a monitor that reopens off-screen is a monitor the
            // parent believes never opened.
            var frame = Prefs.Frame(shape);
            var restorable = frame is { } candidate &&
                DesktopShell.FrameIsRestorable(
                    new WindowFrame(candidate.X, candidate.Y, candidate.Width, candidate.Height),
                    Screens());

            if (frame is { } f && !restorable)
            {
                Log.Warn("ui", $"discarding an unreachable stored {shape} frame {f.X},{f.Y},{f.Width},{f.Height} — opening at the default");
                Prefs.ClearFrame(shape);
            }

            AppWindow.MoveAndResize(restorable && frame is { } good
                ? new RectInt32(good.X, good.Y, good.Width, good.Height)
                : DefaultFrame(shape));
        }
        finally
        {
            _applyingShape = false;
        }

        UpdateUi();
    }

    private void ToggleShape() =>
        ApplyShape(_shape == DesktopShell.ShapeMini ? DesktopShell.ShapeFull : DesktopShell.ShapeMini, persist: true);

    private void RememberFrame()
    {
        if (_fullScreen)
        {
            return; // full screen is not a size worth remembering
        }

        // DESK-25: nor is a minimised one. Win32 parks a minimised window at (-32000,-32000) with a stub
        // size, and AppWindow reports that verbatim — so remembering it here writes a frame that is off
        // every screen, and the next launch opens the monitor where nobody can find it.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            return;
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;

        // Belt and braces: whatever the presenter claims, never store a frame we would refuse to restore.
        if (!DesktopShell.FrameIsRestorable(
                new WindowFrame(position.X, position.Y, size.Width, size.Height),
                Screens()))
        {
            return;
        }

        Prefs.SetFrame(_shape, position.X, position.Y, size.Width, size.Height);
    }

    /// <summary>
    /// DESK-25: every screen's work area, in the physical pixels a stored frame is measured in.
    ///
    /// Indexed by hand rather than with LINQ, and that is not a style choice: `FindAll` hands back a
    /// WinRT vector view whose projection has no usable enumerator, so `.Select(…)` throws at the first
    /// `GetEnumerator` — inside the constructor, before there is a window to show the error in.
    /// </summary>
    private static IReadOnlyList<ScreenArea> Screens()
    {
        var displays = DisplayArea.FindAll();
        var screens = new List<ScreenArea>(displays.Count);
        for (var i = 0; i < displays.Count; i++)
        {
            var work = displays[i].WorkArea;
            screens.Add(new ScreenArea(work.X, work.Y, work.Width, work.Height));
        }

        return screens;
    }

    /// <summary>
    /// DESK-9: where a shape sits the first time, before the parent has moved it. The mini tile is born
    /// in the bottom-right corner of the work area — out of the way of most work, clear of the taskbar,
    /// and where the Mac puts it too, so the two shells match — never stranded in the middle of the
    /// screen, which is only where a brand-new window happens to land. The full window is centred.
    /// Both sit on the screen the window is currently on, and everything is in physical pixels: the
    /// work area, the sizes (DPI-scaled) and the margin alike.
    /// </summary>
    private RectInt32 DefaultFrame(string shape)
    {
        var mini = shape == DesktopShell.ShapeMini;
        var size = mini ? Scaled(360, 202) : Scaled(1100, 700);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        if (mini)
        {
            return MiniCornerFrame(_state.MiniCorner, size, work);
        }

        return new RectInt32(
            work.X + ((work.Width - size.Width) / 2),
            work.Y + ((work.Height - size.Height) / 2),
            size.Width,
            size.Height);
    }

    /// <summary>DESK-8: the tile parked against the chosen corner of the work area, a margin clear of its edges.</summary>
    private RectInt32 MiniCornerFrame(string corner, SizeInt32 size, RectInt32 work)
    {
        var margin = (int)Math.Round(24 * Scale);
        var x = DesktopShell.MiniCornerHugsRight(corner)
            ? work.X + work.Width - size.Width - margin
            : work.X + margin;
        var y = DesktopShell.MiniCornerHugsBottom(corner)
            ? work.Y + work.Height - size.Height - margin
            : work.Y + margin;
        return new RectInt32(x, y, size.Width, size.Height);
    }

    /// <summary>
    /// DESK-8: the parent picked a corner in Settings — move the tile there now (and remember it, so it
    /// lands there next time the mini is shown too), without disturbing the full shape. Cheap on every
    /// UI tick: it only acts when the corner actually changed.
    /// </summary>
    private void ApplyMiniCornerIfChanged()
    {
        var corner = _state.MiniCorner;
        if (corner == _appliedMiniCorner)
        {
            return;
        }

        _appliedMiniCorner = corner;

        var showingMini = _shape == DesktopShell.ShapeMini && !_fullScreen;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        SizeInt32 size;
        if (showingMini)
        {
            var current = AppWindow.Size;
            size = new SizeInt32(current.Width, current.Height); // keep the size the parent gave it
        }
        else if (Prefs.Frame(DesktopShell.ShapeMini) is { } f)
        {
            size = new SizeInt32(f.Width, f.Height);
        }
        else
        {
            size = Scaled(360, 202);
        }

        var target = MiniCornerFrame(corner, size, work);
        Prefs.SetFrame(DesktopShell.ShapeMini, target.X, target.Y, target.Width, target.Height);
        if (showingMini)
        {
            _applyingShape = true;
            try
            {
                AppWindow.MoveAndResize(target);
            }
            finally
            {
                _applyingShape = false;
            }
        }
    }

    /// <summary>
    /// DESK-12: the window takes the camera's shape, so the picture fills it edge to edge and is never
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
        // The title bar's own height, in physical pixels — so it scales with the display like the
        // window it sits on (32 DIPs at 100%, 48 at 150%). Getting this wrong tilts the aspect fit.
        var chrome = _shape == DesktopShell.ShapeMini ? 0 : (int)Math.Round(32 * Scale);
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

    /// <summary>DESK-13 / DESK-6: closing the window closes the window. Monitoring does not notice.</summary>
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return; // a real exit is under way (Shutdown) — let the window actually close
        }

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

    // --- the pointer (LIVE-17, DESK-10/11) -----------------------------------

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
        // PointerExited bubbles. Moving the pointer off one of the tile's own buttons raises it on that
        // child, and it bubbles here — so without this guard the tile would fade and drop its controls
        // while the pointer is still sitting on it (DESK-10/11: the moment the pointer is over it, it is
        // solid). Only a pointer leaving Root itself is the pointer leaving the window.
        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

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

        // DESK-10: close, make-it-full and acknowledge come and go with the pointer. Mute does not —
        // it is what tells a tile too small for words that the sound is off (DESK-8 / LIVE-2).
        MiniTopControls.Opacity = _pointerInside ? 1 : 0;
        MiniTopControls.IsHitTestVisible = _pointerInside;

        // DESK-11: how solid the tile is drawn is the core's decision, not this file's.
        SetWindowOpacity(_shape == DesktopShell.ShapeMini ? _state.MiniOpacity(_pointerInside) : 1.0);
    }

    /// <summary>DESK-11: the mini window fades — through a layered window, which is how Windows does it.</summary>
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

    // --- keyboard (DESK-16) ----------------------------------------------------

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

        // Keep the window's shape consistent with the screen, on EVERY routing change — not just the
        // button paths. Sign-in and the picker are never a tile (DESK-5), and an expiry that dropped us
        // back to sign-in (AUTH-8) has no handler to reshape the window, so it would otherwise strand
        // the login form inside a tiny always-on-top tile. Returning to the viewer restores the user's
        // chosen shape (DESK-9). ApplyShape re-enters UpdateUi with the shape corrected, then this
        // guard is satisfied and it falls through.
        var wantedShape = DesktopShell.WindowShape(screen, Prefs.Shape);
        if (wantedShape != _shape && !_applyingShape)
        {
            ApplyShape(Prefs.Shape, restoreFrame: true);
            return;
        }

        // DESK-8: a corner chosen in Settings snaps the tile there (a cheap no-op unless it changed).
        ApplyMiniCornerIfChanged();

        var viewer = screen == "viewer";
        var mini = viewer && _shape == DesktopShell.ShapeMini;

        LoginPanel.Visibility = screen == "login" ? Visibility.Visible : Visibility.Collapsed;
        CamerasPanel.Visibility = screen == "devices" ? Visibility.Visible : Visibility.Collapsed;
        ViewerChrome.Visibility = viewer && !mini ? Visibility.Visible : Visibility.Collapsed;
        MiniChrome.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        Video.Visibility = viewer ? Visibility.Visible : Visibility.Collapsed;

        // DESK-16 / AUTH-11: arriving on the login screen puts the cursor in the first field, so a
        // parent (or a password manager) types straight away, no click first. On the transition only —
        // never on a state tick, which would yank focus mid-type. The captcha and code fields focus
        // themselves when they appear (HandleSignInStepAsync), so only the credentials form is here.
        if (screen == "login" && _lastScreen != "login" && CredentialsGroup.Visibility == Visibility.Visible)
        {
            UsernameBox.Focus(FocusState.Programmatic);
        }

        _lastScreen = screen;

        if (screen == "devices" && !_camerasLoaded)
        {
            _camerasLoaded = true;
            _ = LoadCamerasAsync();
        }

        // AUTH-8 / BG-8: an expired session routes the parent back to sign-in (AppState does that), and
        // the reason it carried is shown there once, so the screen is never a bare form with no
        // explanation for why the watch ended.
        if (screen == "login" && _state.TakeSessionExpiredMessage() is { } expired)
        {
            ShowLoginError(expired);
        }

        var color = StatusColor();
        StatusDot.Fill = new SolidColorBrush(color);
        MiniStatusDot.Fill = new SolidColorBrush(color);
        StatusLineText.Text = _state.StatusLine;

        // In the mini tile the warnings that live in the (collapsed) full chrome have nowhere to
        // appear, so its one status line carries them — a tile too small for the full sentence must
        // still never be a silent black square or a calm-looking tile over a monitor that was down.
        // Priority: a sleep outage to be read (DESK-21) > a camera we cannot reach at all (DESK-24) >
        // no decoder (DESK-22) > the feed's own state. Not seeing the camera outranks not decoding it:
        // one is a monitor that is watching nothing, the other only a missing picture.
        // These warnings are SHORT forms — the full sentence lives in the tray menu and the full view;
        // the tile only has to say enough that the parent knows to look (and the text is trimmed so it
        // can never grow into the buttons beside it).
        var miniStatus = _state.SleepOutage != null ? "Monitor was down while asleep"
            : _state.ConnectionBlocked && !_state.NetworkDown ? DesktopShell.FirewallAdviceShort
            : _state.VideoUnavailable ? "No video — needs HEVC support"
            : _state.StatusText;
        MiniStatusText.Text = _state.Muted ? $"{miniStatus} · muted" : miniStatus;
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

        // BG-14: Start appears only for a monitor that stopped working. There is no Stop to sit
        // beside it — so its separator goes with it rather than floating at the head of the row.
        StartButton.Visibility = _state.CanResume ? Visibility.Visible : Visibility.Collapsed;
        StartSeparator.Visibility = StartButton.Visibility;

        AlarmBanner.Visibility = _state.Alarming ? Visibility.Visible : Visibility.Collapsed;
        AlarmText.Text = _state.AlarmText;
        MiniAcknowledge.Visibility = _state.Alarming ? Visibility.Visible : Visibility.Collapsed;
        MiniAlarmBorder.BorderThickness = new Thickness(_state.Alarming ? 2 : 0);
        MiniLevelBar.Visibility = _state.Running ? Visibility.Visible : Visibility.Collapsed; // LIVE-6

        OutageBanner.Visibility = _state.SleepOutage != null ? Visibility.Visible : Visibility.Collapsed;
        OutageText.Text = _state.SleepOutage ?? string.Empty;

        FeedbackBanner.Visibility = _state.AskingCryFeedback ? Visibility.Visible : Visibility.Collapsed;
        StartupBanner.Visibility = _state.StartupOfferPending && viewer
            ? Visibility.Visible
            : Visibility.Collapsed;
        StartupErrorText.Text = _state.StartupError ?? string.Empty;
        StartupErrorText.Visibility = _state.StartupError != null ? Visibility.Visible : Visibility.Collapsed;

        SleepWarning.Visibility = _state.SleepUnprotected ? Visibility.Visible : Visibility.Collapsed;
        NetworkWarning.Visibility = _state.NetworkDown ? Visibility.Visible : Visibility.Collapsed;
        VideoWarning.Visibility = _state.VideoUnavailable ? Visibility.Visible : Visibility.Collapsed;

        // DESK-24: only when the network itself is up — if the PC is offline, LIVE-13 has already said
        // the truer thing, and two explanations for one silence is worse than one.
        FirewallWarning.Visibility = _state.ConnectionBlocked && !_state.NetworkDown
            ? Visibility.Visible
            : Visibility.Collapsed;
        FirewallWarningText.Text = DesktopShell.FirewallAdvice;

        // LIVE-18: the control says which picture is being asked for, on the button itself — a menu you
        // have to open to learn the current state is a state the parent does not know.
        var sd = _state.Settings.VideoQuality == Settings.QualitySd;
        QualityLabel.Text = sd ? "SD" : "HD";
        QualitySdItem.IsChecked = sd;
        QualityHdItem.IsChecked = !sd;

        // LIVE-14: the display stays awake while a window is showing a live feed — and only then.
        PowerRequests.HoldDisplay(AppWindow.IsVisible && _state.Status == Statuses.Live);

        UpdateChrome();
        RefreshTray();
    }

    private void UpdateLevelBar()
    {
        // The full window's bar and the mini tile's bar (LIVE-6) — the same reading, drawn on both, so
        // whichever shape is up shows the room level. Only one is on screen at a time.
        ApplyLevel(LevelBar, LevelFill, LevelMark);
        ApplyLevel(MiniLevelBar, MiniLevelFill, MiniLevelMark);
    }

    private void ApplyLevel(Grid bar, Rectangle fill, Rectangle mark)
    {
        var width = bar.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(_state.Level / Math.Max(_state.LevelMax, 1), 0, 1);
        var past = _state.AlarmEnabled && _state.Level >= _state.ThresholdDb;
        fill.Width = width * fraction;
        fill.Fill = new SolidColorBrush(past
            ? Color.FromArgb(0xFF, 0xE5, 0x53, 0x3B)
            : Color.FromArgb(0xFF, 0x4C, 0xD9, 0x64));

        // ALRM-12: where it will ring. The one number on this screen that decides whether a parent is
        // woken — including any learned adjustment (ALRM-16).
        mark.Visibility = _state.AlarmEnabled ? Visibility.Visible : Visibility.Collapsed;
        var markFraction = Math.Clamp(_state.ThresholdDb / Math.Max(_state.LevelMax, 1), 0, 1);
        mark.Margin = new Thickness((width * markFraction) - 1, 0, 0, 0);
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
    /// BG-14: Start, and no Stop. It is here only for a monitor that failed on its own (WATCH-11) —
    /// which must be recoverable right where the parent is looking, not by exiting the app.
    /// </summary>
    private void OnStart(object sender, RoutedEventArgs e) => _state.Start();

    private void OnCheckForUpdates(object sender, RoutedEventArgs e) =>
        _ = CheckForUpdateAsync(askedByUser: true); // UPD-9

    private void OnExit(object sender, RoutedEventArgs e) => _ = ConfirmExitAsync();

    private void OnToggleMute(object sender, RoutedEventArgs e) => _state.ToggleMute();

    /// <summary>LIVE-18: ask the camera for the other picture. The feed reconnects; the menu said so.</summary>
    private void OnQuality(object sender, RoutedEventArgs e) =>
        _state.SetVideoQuality(ReferenceEquals(sender, QualitySdItem) ? Settings.QualitySd : Settings.QualityHd);

    private void OnAcknowledge(object sender, RoutedEventArgs e) => _state.Acknowledge();

    private void OnCryYes(object sender, RoutedEventArgs e) => _state.SubmitCryFeedback(true);

    private void OnCryNo(object sender, RoutedEventArgs e) => _state.SubmitCryFeedback(false);

    private void OnCryDismiss(object sender, RoutedEventArgs e) => _state.DismissCryFeedback();

    private void OnDismissOutage(object sender, RoutedEventArgs e) => _state.DismissSleepOutage();

    private void OnEnableStartup(object sender, RoutedEventArgs e) => _state.SetStartWithWindows(true);

    private void OnDeclineStartup(object sender, RoutedEventArgs e) => _state.AnswerStartupOffer();

    private void OnMakeMini(object sender, RoutedEventArgs e) => ApplyShape(DesktopShell.ShapeMini, persist: true);

    private void OnMakeFull(object sender, RoutedEventArgs e) => ApplyShape(DesktopShell.ShapeFull, persist: true);

    private void OnHideWindow(object sender, RoutedEventArgs e) => HideWindow();

    private void OnSwitchCamera(object sender, RoutedEventArgs e)
    {
        _camerasLoaded = false;
        _state.SwitchCamera();
        // The picker forces the full shape for display (DESK-5) — but pass the user's preference, not
        // ShapeFull, so their mini choice survives the trip through the picker (DESK-9).
        ApplyShape(Prefs.Shape, restoreFrame: true);
    }

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        _camerasLoaded = false;
        _state.SignOut();
        ApplyShape(Prefs.Shape, restoreFrame: true); // full for display, mini preference kept (DESK-9)
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
        OpenUri("ms-settings:network"); // LIVE-13: the shortest route back to the camera's network

    /// <summary>DESK-22: the shortest route to a picture — the free extension, in the Store.</summary>
    private void OnInstallHevc(object sender, RoutedEventArgs e) =>
        OpenUri("ms-windows-store://pdp/?ProductId=9n4wgh0z6vhq");

    /// <summary>
    /// DESK-24: the way to the rule that lets the camera answer. The app cannot add it itself — that
    /// needs an administrator, and this is a per-user install by design — so it opens the place where a
    /// parent can, rather than describing it and leaving them to hunt.
    /// </summary>
    private void OnOpenFirewallSettings(object sender, RoutedEventArgs e) =>
        OpenUri("windowsdefender://network/");

    /// <summary>
    /// Hand a URI to the shell's own handler. Not <c>Launcher.LaunchUriAsync</c>: in an unpackaged app
    /// that can silently no-op, and both callers are dead-end escapes a parent is relying on — the way
    /// back to the camera's network (LIVE-13) and the way to a picture (DESK-22). `UseShellExecute`
    /// launches the protocol handler the boring, certain way.
    /// </summary>
    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn("ui", $"could not open {uri}: {e.Message}");
        }
    }

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

    /// <summary>
    /// DESK-16: Enter submits the form from any login field, the way it does in every other login on
    /// the platform — a password manager fills the fields and one keystroke signs in, no reach for the
    /// mouse at 3am. Which step it submits (credentials, captcha, code) is OnSignIn's own business.
    /// </summary>
    private void OnLoginFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !_busy)
        {
            e.Handled = true;
            OnSignIn(sender, e);
        }
    }

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
        catch (Exception ex)
        {
            // This is an async void handler: an exception here has nowhere to go but the global
            // last-resort handler, and the parent would be left staring at a spinner. A gateway can
            // return a malformed captcha PNG (LoadImageAsync throws) among other things — so any failure
            // becomes a readable error with the fields still editable (AUTH-9).
            Log.Warn("ui", $"sign-in failed: {ex.Message}");
            ShowLoginError("Something went wrong signing in. Please try again.");
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

        if (CameraSelection.AutoSelectsSingle(cameras.Count))
        {
            // CAM-6: one camera, no choice to make — open it straight away, never a list of one.
            ChooseCamera(cameras[0]);
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

        ChooseCamera(camera);
    }

    /// <summary>CAM-2/CAM-6: choosing a camera — by tap or auto-picked when it is the only one — opens its feed.</summary>
    private void ChooseCamera(CameraInfo camera)
    {
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
        // A version put in place by an earlier run has already taken over by now — that happens in
        // Program.Main, before any of this exists (UPD-10). All that is left is to look for a new one.
        //
        // UPD-11: unless the parent has switched the automatic check off. A manual check (UPD-9) still
        // works — this gate is only on the check the app runs on its own.
        if (!_state.AutoUpdateEnabled)
        {
            Log.Info("update", "automatic updates are off — skipping the launch check");
            return;
        }

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

        var answer = await AskAsync(new ContentDialog
        {
            Title = $"Baby Monitor {version} is installed.",
            Content = _state.Running
                ? "It will run the next time you open Baby Monitor. Restarting now takes a few " +
                  "seconds, during which the baby is not monitored — monitoring resumes by itself " +
                  "afterwards."
                : "It will run the next time you open Baby Monitor.",
            PrimaryButtonText = "Restart now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close,
        });

        if (answer != ContentDialogResult.Primary)
        {
            // Including a question that could not be asked. The app never restarts itself, so silence
            // means "later" — and later is a real answer: it is already on disk, and it runs next time.
            Log.Info("update", $"not restarting — {version} will run at the next launch");
            return;
        }

        if (_updater.Staged is not { } staged)
        {
            return;
        }

        // Install re-verifies and extracts the staged zip (tens of MB) and waits out the new process's
        // first second — seconds of work that must not freeze the window the parent just clicked. Off
        // the UI thread, then finish the hand-over back on it.
        var launched = await Task.Run(() => Updater.Install(staged));
        if (launched)
        {
            _state.Stop(); // they asked for this restart; the watch resumes on the other side
            Shutdown();    // same clean exit as user Exit: tray icon removed, close not cancelled
        }
    }

    private async Task TellUserAsync(string title, string body) =>
        await AskAsync(new ContentDialog
        {
            Title = title,
            Content = body,
            CloseButtonText = "OK",
        });

    // --- odds and ends --------------------------------------------------------

    /// <summary>
    /// The one door every dialog in this app goes through.
    ///
    /// Two things it guards, and both would otherwise be a crash or a lie:
    ///  - **only one ContentDialog may be open at a time.** A second `ShowAsync` throws, and the two
    ///    that can genuinely collide are the update's "restart now?" and Exit's "are you sure?" —
    ///    the two questions that must never be lost.
    ///  - **it needs a loaded window.** The launch-time update check can answer before the window has
    ///    a XamlRoot; with none, there is nothing to put the question on.
    ///
    /// A dialog that could not be shown answers <see cref="ContentDialogResult.None"/>, and every
    /// caller reads that as "no": the app does not restart, and it does not exit. Never act on a
    /// question nobody was asked.
    /// </summary>
    private async Task<ContentDialogResult> AskAsync(ContentDialog dialog)
    {
        await _dialogLock.WaitAsync().ConfigureAwait(true);
        try
        {
            ShowWindow(); // a question nobody can see is not a question
            if (Root.XamlRoot == null)
            {
                Log.Warn("ui", $"could not ask '{dialog.Title}' — the window has no XAML root yet");
                return ContentDialogResult.None;
            }

            dialog.XamlRoot = Root.XamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception e)
        {
            Log.Error("ui", $"could not show the dialog '{dialog.Title}': {e.Message}", e);
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogLock.Release();
        }
    }

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// `AppWindow.Resize` takes physical pixels; XAML lays out in DIPs. On a 150% laptop — the common
    /// case — a tile asked for "360×202" comes out 240×135 effective, and the bottom row (Acknowledge
    /// included, the one control that must never be unreachable) is clipped. So every default size is
    /// scaled by the window's DPI first. Stored frames are physical in and physical out, so they are
    /// left alone.
    /// </summary>
    private double Scale => Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);

    private SizeInt32 Scaled(int width, int height) =>
        new((int)Math.Round(width * Scale), (int)Math.Round(height * Scale));
}
