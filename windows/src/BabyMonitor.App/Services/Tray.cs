using System.Runtime.InteropServices;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App.Services;

/// <summary>
/// One item in the tray menu. A null <see cref="Action"/> makes it a disabled label; a non-null
/// <see cref="Children"/> makes it a submenu.
/// </summary>
public sealed record TrayItem(
    string Text,
    Action? Action = null,
    bool Checked = false,
    bool Separator = false,
    IReadOnlyList<TrayItem>? Children = null)
{
    public static TrayItem Divider => new(string.Empty, Separator: true);

    public static TrayItem Label(string text) => new(text);

    public static TrayItem Submenu(string text, IReadOnlyList<TrayItem> children) =>
        new(text, Children: children);
}

/// <summary>
/// DESK-1/2 — the app lives in the notification area, and this is it.
///
/// Hand-rolled on Win32 rather than taken from a package, because everything it needs is Win32: a
/// real `Shell_NotifyIcon`, a real `TrackPopupMenu`, and a real window procedure. That buys the
/// behaviour a Windows user already knows — either mouse button opens the menu, clicking away
/// dismisses it, the icon survives an Explorer restart — which is what DESK-2 asks for.
///
/// It also owns the app's hidden message window, which is where **the sleep and wake broadcasts
/// arrive** (DESK-21). A monitor that cannot notice the machine slept is a monitor that lies about
/// the night.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmDestroy = 0x0002;
    private const int WmCommand = 0x0111;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmTrayCallback = 0x0400 + 1; // WM_APP + 1
    private const int WmRbuttonUp = 0x0205;
    private const int WmLbuttonUp = 0x0202;
    private const int WmLbuttonDblClk = 0x0203;
    private const int WmNull = 0x0000;

    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfChecked = 0x00000008;
    private const uint MfGrayed = 0x00000001;
    private const uint MfPopup = 0x00000010;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private readonly WndProc _wndProc; // held so the GC cannot collect the delegate under Windows
    private readonly List<Action> _commands = new();
    private readonly uint _taskbarCreated;
    private readonly Action _onOpen;
    private readonly Func<bool> _onLeftClick;
    private readonly Func<IReadOnlyList<TrayItem>> _menu;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip = "Baby Monitor";
    private string _iconPath = string.Empty;
    private bool _added;

    /// <summary>DESK-21: the machine is about to sleep, and nothing we can do will stop it.</summary>
    public event Action? SystemSuspending;

    /// <summary>DESK-21: it woke. The outage is reported, never quietly reconnected.</summary>
    public event Action? SystemResumed;

    /// <param name="onLeftClick">
    /// DESK-28: a left-click on the icon, given the chance to consume itself before the menu opens.
    /// It returns true when it has handled the click (acknowledging a ringing alarm — the one way to
    /// silence it that needs neither the click-through tile nor the menu); false falls through to the
    /// menu, so a plain left-click still opens it the way DESK-2 promises whenever nothing is ringing.
    /// A right-click always opens the menu, whatever this returns.
    /// </param>
    public TrayIcon(Func<IReadOnlyList<TrayItem>> menu, Action onOpen, Func<bool> onLeftClick)
    {
        _menu = menu;
        _onOpen = onOpen;
        _onLeftClick = onLeftClick;
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        CreateHostWindow();
    }

    /// <summary>
    /// DESK-1: the state, at a glance. The icon and the tooltip change with the feed.
    ///
    /// Called on every state change — and the level meter changes state twenty times a second. So
    /// this does nothing at all unless something a human could see has actually moved: reloading an
    /// HICON and poking the shell at 20 Hz would flicker the tray and leak a handle a tick.
    /// </summary>
    public void Update(string iconPath, string tooltip)
    {
        var trimmed = tooltip.Length > 127 ? tooltip[..127] : tooltip; // the shell truncates at 128
        if (_added && trimmed == _tooltip && iconPath == _iconPath)
        {
            return;
        }

        _tooltip = trimmed;

        if (iconPath != _iconPath)
        {
            var icon = LoadIcon(iconPath);
            if (icon != IntPtr.Zero)
            {
                var previous = _icon;
                _icon = icon;
                _iconPath = iconPath;
                if (previous != IntPtr.Zero)
                {
                    DestroyIcon(previous);
                }
            }
        }

        Send(_added ? NimModify : NimAdd);
    }

    public void Dispose()
    {
        if (_added)
        {
            Send(NimDelete);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void CreateHostWindow()
    {
        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "BabyMonitorTrayHost",
        };
        RegisterClassEx(ref wc);

        // A real (never-shown) top-level window rather than a message-only one: TrackPopupMenu needs a
        // foreground-able owner, and a HWND_MESSAGE window is not one.
        _hwnd = CreateWindowEx(
            0,
            wc.lpszClassName,
            "Baby Monitor",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            wc.hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Log.Error("app", $"could not create the tray host window (Win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WmTrayCallback:
                    switch ((int)lParam)
                    {
                        case WmLbuttonUp:
                            // DESK-28: a left-click silences a ringing alarm if there is one, and only
                            // then — otherwise it opens the menu like a right-click (DESK-2).
                            if (!_onLeftClick())
                            {
                                ShowMenu();
                            }

                            return IntPtr.Zero;
                        case WmRbuttonUp:
                            ShowMenu();
                            return IntPtr.Zero;
                        case WmLbuttonDblClk:
                            _onOpen();
                            return IntPtr.Zero;
                    }

                    break;

                case WmCommand:
                {
                    var id = (int)(wParam.ToInt64() & 0xffff);
                    if (id > 0 && id <= _commands.Count)
                    {
                        _commands[id - 1]();
                    }

                    return IntPtr.Zero;
                }

                case WmPowerBroadcast:
                    switch ((int)wParam)
                    {
                        case PbtApmSuspend:
                            SystemSuspending?.Invoke();
                            break;
                        case PbtApmResumeAutomatic:
                        case PbtApmResumeSuspend:
                            SystemResumed?.Invoke();
                            break;
                    }

                    return new IntPtr(1);

                case WmDestroy:
                    return IntPtr.Zero;
            }

            if (msg == _taskbarCreated && _added)
            {
                // Explorer restarted and took every tray icon with it. Put ours back — a monitor that
                // has quietly lost its only visible state is a monitor nobody can check.
                Log.Info("app", "Explorer restarted — re-adding the tray icon");
                _added = false;
                Send(NimAdd);
                return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            // Nothing in a window procedure may throw into Windows.
            Log.Error("ui", $"the tray window procedure threw: {e.Message}", e);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = _menu();
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _commands.Clear();
            Fill(menu, items);

            GetCursorPos(out var point);

            // The dance every tray app does: take the foreground so the menu dismisses when the user
            // clicks away, and post a null message afterwards so Windows lets go of it again.
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (chosen > 0 && chosen <= _commands.Count)
            {
                _commands[chosen - 1]();
            }
        }
        finally
        {
            DestroyMenu(menu); // takes its submenus with it
        }
    }

    /// <summary>
    /// Builds one level of the menu, recursing into submenus (CAM-4's camera list is one). A submenu
    /// carries no command id of its own — Windows never sends WM_COMMAND for the parent of a popup.
    /// </summary>
    private void Fill(IntPtr menu, IReadOnlyList<TrayItem> items)
    {
        foreach (var item in items)
        {
            if (item.Separator)
            {
                AppendMenu(menu, MfSeparator, IntPtr.Zero, null);
                continue;
            }

            if (item.Children != null)
            {
                var submenu = CreatePopupMenu();
                if (submenu == IntPtr.Zero)
                {
                    continue;
                }

                Fill(submenu, item.Children);
                AppendMenu(menu, MfPopup, submenu, Escape(item.Text));
                continue;
            }

            uint flags = MfString;
            if (item.Checked)
            {
                flags |= MfChecked;
            }

            if (item.Action == null)
            {
                flags |= MfGrayed; // a label, not a control
            }

            _commands.Add(item.Action ?? (() => { }));
            AppendMenu(menu, flags, new IntPtr(_commands.Count), Escape(item.Text));
        }
    }

    /// <summary>
    /// A single `&amp;` in a Win32 menu string is a keyboard mnemonic — the next character is underlined
    /// and swallowed. Camera names are user data (DESK-2's submenu), so "Nursery &amp; Hall" would show as
    /// "Nursery Hall". Doubling restores the literal ampersand.
    /// </summary>
    private static string? Escape(string? text) => text?.Replace("&", "&&");

    private void Send(uint message)
    {
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifIcon | NifTip | NifMessage,
            uCallbackMessage = WmTrayCallback,
            hIcon = _icon,
            szTip = _tooltip,
            // ByValTStr will not marshal a null, and a struct field left null here would take the
            // tray icon — and with it every visible sign of the monitor — down with an exception.
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        if (!Shell_NotifyIcon(message, ref data))
        {
            Log.Warn("ui", $"Shell_NotifyIcon({message}) failed (Win32 error {Marshal.GetLastWin32Error()})");
            return;
        }

        if (message == NimAdd)
        {
            _added = true;
        }
    }

    private static IntPtr LoadIcon(string path)
    {
        const uint imageIcon = 1;
        const uint lrLoadFromFile = 0x00000010;
        const uint lrDefaultSize = 0x00000040;
        var icon = LoadImage(IntPtr.Zero, path, imageIcon, 0, 0, lrLoadFromFile | lrDefaultSize);
        if (icon == IntPtr.Zero)
        {
            Log.Warn("ui", $"could not load the tray icon from {path}");
        }

        return icon;
    }

    // --- Win32 -----------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
