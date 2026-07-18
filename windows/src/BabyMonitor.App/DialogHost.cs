using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App;

/// <summary>
/// **DESK-29: a question the app must ask is shown in its own window, in the middle of the screen.**
///
/// Every confirmation lives here rather than on the monitor window's content, and DESK-28 is why.
/// The mini tile can be locked click-through and non-activating so a game plays straight through it —
/// and a dialog put on *that* window inherits all of it: it is the size of the tile, clicks fall
/// through it, and it can never take focus. "Exit?" and "Install the update and restart?" are the two
/// questions that must never be lost, and on a locked tile they could not be answered at all. So a
/// question hosts itself here instead — a fresh, ordinary, centred, always-on-top window that owes
/// nothing to the tile's styles and is interactable whatever shape the monitor window is in.
///
/// The window **is** the dialog: its content is the card, edge to edge, so there is no black scrim and
/// no empty frame around it — a `ContentDialog` would have drawn both. This is what the Mac gets for
/// free from NSAlert: a self-contained, centred panel that is only as big as its question.
///
/// One host can carry several questions in turn before it is disposed — the manual update check swaps
/// "checking…" for its result on the same window (UPD-9).
/// </summary>
internal sealed class DialogHost : IDisposable
{
    private readonly Window _window;
    private readonly TextBlock _title;
    private readonly ContentPresenter _body;
    private readonly StackPanel _buttons;

    private TaskCompletionSource<bool>? _pending;

    private DialogHost(Window window, TextBlock title, ContentPresenter body, StackPanel buttons)
    {
        _window = window;
        _title = title;
        _body = body;
        _buttons = buttons;
    }

    /// <summary>
    /// Put a centred dialog window on screen and wait until it can carry content (it has laid out).
    /// <paramref name="near"/> is the monitor window, used only to pick the display the question appears
    /// on — the same screen the parent is looking at.
    /// </summary>
    public static async Task<DialogHost> CreateAsync(Window near)
    {
        var title = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.White),
        };
        var body = new ContentPresenter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        // The card *is* the window: dark surface, padding, title at the top, the body filling the middle,
        // the buttons pinned bottom-right — no scrim, no margin.
        var grid = new Grid
        {
            RequestedTheme = ElementTheme.Dark, // the app is dark everywhere (UI-1)
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B)),
            Padding = new Thickness(28),
            RowSpacing = 16,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(title, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(title);
        grid.Children.Add(body);
        grid.Children.Add(buttons);

        var window = new Window { Content = grid, Title = "Baby Monitor" };

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
        var size = new SizeInt32((int)(460 * scale), (int)(220 * scale));
        appWindow.Resize(size);

        // Strip every window-frame style, so the dialog is just its dark card — no title bar, no border,
        // no drop line. SetBorderAndTitleBar leaves a thin edge; this removes it.
        var style = GetWindowLong(hwnd, GwlStyle);
        SetWindowLong(hwnd, GwlStyle, style & ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu | WsDlgFrame | WsBorder));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);

        // Centre on the work area of the display the monitor window is on, so the question lands where
        // the parent already is rather than on the primary monitor by default.
        var display = DisplayArea.GetFromWindowId(near.AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        appWindow.Move(new PointInt32(
            work.X + ((work.Width - size.Width) / 2),
            work.Y + ((work.Height - size.Height) / 2)));

        var self = new DialogHost(window, title, body, buttons);
        grid.KeyDown += self.OnKeyDown; // Enter → primary, Escape → close

        var ready = new TaskCompletionSource();
        if (grid.XamlRoot != null)
        {
            ready.TrySetResult();
        }
        else
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                grid.Loaded -= OnLoaded;
                ready.TrySetResult();
            }

            grid.Loaded += OnLoaded;
        }

        window.Activate();
        SetForegroundWindow(hwnd); // a question the parent cannot see is not a question
        await ready.Task.ConfigureAwait(true);
        return self;
    }

    /// <summary>Ask a two-answer question; true when the primary button was chosen.</summary>
    public Task<bool> ConfirmAsync(string title, string body, string primary, string close)
    {
        _title.Text = title;
        _body.Content = BodyText(body);
        return ArmButtons(primary, close).Task;
    }

    /// <summary>State something with a single acknowledgement.</summary>
    public Task NoticeAsync(string title, string body)
    {
        _title.Text = title;
        _body.Content = BodyText(body);
        return ArmButtons(primary: null, close: "OK").Task;
    }

    /// <summary>
    /// Show an indeterminate-progress view — a spinner the parent cannot dismiss except with the Cancel
    /// button, if one is offered. Does not await: the caller replaces it (with a result, or by disposing
    /// the host) when the work behind it finishes.
    /// </summary>
    public void ShowProgress(string title, string body, Action? onCancel)
    {
        Answer(false);

        _title.Text = title;
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(BodyText(body));
        panel.Children.Add(new ProgressBar { IsIndeterminate = true });
        _body.Content = panel;

        _buttons.Children.Clear();
        if (onCancel != null)
        {
            var cancel = new Button { Content = "Cancel", MinWidth = 88 };
            cancel.Click += (_, _) => onCancel();
            _buttons.Children.Add(cancel);
        }
    }

    public void Dispose()
    {
        Answer(false);
        try
        {
            _window.Close();
        }
        catch (Exception e)
        {
            Log.Warn("ui", $"could not close the dialog window: {e.Message}");
        }
    }

    private static TextBlock BodyText(string body) => new()
    {
        Text = body,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0)),
    };

    private TaskCompletionSource<bool> ArmButtons(string? primary, string close)
    {
        Answer(false);
        var tcs = new TaskCompletionSource<bool>();
        _pending = tcs;

        _buttons.Children.Clear();
        var closeButton = new Button { Content = close, MinWidth = 88 };
        closeButton.Click += (_, _) => Answer(false);
        _buttons.Children.Add(closeButton);

        if (primary != null)
        {
            var primaryButton = new Button
            {
                Content = primary,
                MinWidth = 88,
                Style = Application.Current.Resources["AccentButtonStyle"] as Style,
            };
            primaryButton.Click += (_, _) => Answer(true);
            _buttons.Children.Add(primaryButton);
            primaryButton.Loaded += (s, _) => ((Button)s).Focus(FocusState.Programmatic);
        }
        else
        {
            closeButton.Loaded += (s, _) => ((Button)s).Focus(FocusState.Programmatic);
        }

        return tcs;
    }

    private void Answer(bool primary)
    {
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(primary);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_pending == null)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            Answer(false);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            Answer(_buttons.Children.Count > 1); // Enter takes the primary answer when there is one
        }
    }

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsSysMenu = 0x00080000;
    private const int WsDlgFrame = 0x00400000;
    private const int WsBorder = 0x00800000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
