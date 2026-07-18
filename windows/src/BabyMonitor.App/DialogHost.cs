using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App;

/// <summary>
/// **DESK-29: a question the app must ask is shown in its own window, in the middle of the screen.**
///
/// Every confirmation lives here rather than on the monitor window's content, and DESK-28 is why.
/// The mini tile can be locked click-through and non-activating so a game plays straight through it —
/// and a <see cref="ContentDialog"/> put on *that* window inherits all of it: it is the size of the
/// tile, clicks fall through it, and it can never take focus. "Exit?" and "Restart into the update?"
/// are the two questions that must never be lost, and on a locked tile they could not be answered at
/// all. So a dialog hosts itself here instead — a fresh, ordinary, centred, always-on-top window that
/// owes nothing to the tile's styles and is interactable whatever shape the monitor window is in, or
/// whether it is even on screen.
///
/// This is what the Mac gets for free: there every one of these is an <c>NSAlert</c>, already its own
/// centred window. On a PC the app hosts them in one.
///
/// The host can carry several dialogs in turn before it is disposed — the manual update check shows a
/// "checking…" dialog and then swaps it for the result on the same window (UPD-9).
/// </summary>
internal sealed class DialogHost : IDisposable
{
    private readonly Window _window;
    private readonly Grid _root;

    private DialogHost(Window window, Grid root)
    {
        _window = window;
        _root = root;
    }

    /// <summary>
    /// Put a centred host window on screen and wait until it can carry a dialog (its content has laid
    /// out and so has a <see cref="XamlRoot"/>). <paramref name="near"/> is the monitor window, used
    /// only to pick the display the question appears on — the same screen the parent is looking at.
    /// </summary>
    public static async Task<DialogHost> CreateAsync(Window near)
    {
        // Black, because a ContentDialog draws its own dimming smoke over the whole of its XamlRoot;
        // over black that reads as an ordinary modal scrim with the dialog floating in the middle,
        // rather than a stray panel.
        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        var window = new Window { Content = root, Title = "Baby Monitor" };

        var appWindow = window.AppWindow;
        appWindow.IsShownInSwitchers = false; // transient — never a window to Alt-Tab to
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true; // above the always-on-top mini tile
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        var hwnd = WindowNative.GetWindowHandle(window);
        var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        var size = new SizeInt32((int)(560 * scale), (int)(420 * scale));
        appWindow.Resize(size);

        // Centre on the work area of the display the monitor window is on, so the question lands where
        // the parent already is rather than on the primary monitor by default.
        var display = DisplayArea.GetFromWindowId(near.AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        appWindow.Move(new PointInt32(
            work.X + ((work.Width - size.Width) / 2),
            work.Y + ((work.Height - size.Height) / 2)));

        var ready = new TaskCompletionSource();
        if (root.XamlRoot != null)
        {
            ready.TrySetResult();
        }
        else
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                root.Loaded -= OnLoaded;
                ready.TrySetResult();
            }

            root.Loaded += OnLoaded;
        }

        window.Activate();
        SetForegroundWindow(hwnd); // a question the parent cannot see is not a question
        await ready.Task.ConfigureAwait(true);
        return new DialogHost(window, root);
    }

    /// <summary>Show a dialog on this host and return its answer.</summary>
    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = _root.XamlRoot;
        return await dialog.ShowAsync();
    }

    public void Dispose()
    {
        try
        {
            _window.Close();
        }
        catch (Exception e)
        {
            Log.Warn("ui", $"could not close the dialog window: {e.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
