using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.UI.ViewManagement;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App.Services;

/// <summary>
/// BG-12w / WIN-10: a sleeping PC runs nothing at all, so while monitoring is on we ask Windows not
/// to idle-sleep, and while a window is showing a live feed we ask it not to blank the screen.
///
/// This cannot stop the sleep a user *asks* for — the lid, the power button, Start → Sleep. Nothing
/// an app can do will. That gap is real, it is spec'd (WIN-11), and the app reports the outage on
/// wake rather than pretending the night was covered.
///
/// **`SetThreadExecutionState` is thread-affine**: the request belongs to the thread that made it and
/// dies with it. Everything here is therefore called from the UI thread — the one thread that lives
/// as long as the app does. Call it from a task-pool thread and the request evaporates when that
/// thread is recycled, silently, and the PC sleeps through the night.
/// </summary>
public static class PowerRequests
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000,
    }

    private static bool _systemHeld;
    private static bool _displayHeld;

    /// <summary>
    /// BG-12w: hold the system awake for as long as monitoring runs. Returns false if Windows refused —
    /// the caller must then say so rather than appear to monitor.
    /// </summary>
    public static bool HoldSystem(bool wanted)
    {
        if (_systemHeld == wanted)
        {
            return true;
        }

        _systemHeld = wanted;
        var ok = SetThreadExecutionState(Requested()) != 0;
        Log.Info("app", wanted
            ? $"sleep inhibitor {(ok ? "held" : "REFUSED")} — the PC {(ok ? "will not idle-sleep while monitoring" : "may sleep and stop monitoring")}"
            : "sleep inhibitor released — the PC may idle-sleep again");
        return ok;
    }

    /// <summary>
    /// LIVE-14 / WIN-10: keep the display on, but only while there is something to look at. A monitor
    /// nobody is watching has no business burning a screen all night.
    /// </summary>
    public static void HoldDisplay(bool wanted)
    {
        if (_displayHeld == wanted)
        {
            return;
        }

        _displayHeld = wanted;
        SetThreadExecutionState(Requested());
        Log.Debug("app", $"display-wake request {(wanted ? "held" : "released")}");
    }

    private static ExecutionState Requested()
    {
        var state = ExecutionState.Continuous;
        if (_systemHeld)
        {
            state |= ExecutionState.SystemRequired;
        }

        if (_displayHeld)
        {
            state |= ExecutionState.DisplayRequired;
        }

        return state;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState esFlags);
}

/// <summary>
/// WIN-8: start with Windows, so a PC that restarts overnight comes back to a running monitor
/// (BG-13w).
///
/// The Run key is the plain, visible, user-scoped way to do this: it needs no installer, no admin,
/// and the user can see and undo it in Task Manager → Startup, which is where a Windows user looks.
/// **The app never turns it on by itself.**
/// </summary>
public static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BabyMonitor";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Warn("app", $"could not read the startup setting: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>Returns a readable reason on failure — never throws into the UI, never silently no-ops.</summary>
    public static string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new IOException("the startup key could not be opened");

            if (enabled)
            {
                var exe = Environment.ProcessPath ?? throw new IOException("the app's own path is unknown");
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info("app", $"start with Windows → {enabled}");
            return null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn("app", $"could not change the startup setting: {e.Message}");
            return e.Message;
        }
    }
}

/// <summary>
/// LIVE-13: the camera is reachable only on its own network. A PC with no network at all cannot reach
/// it, and must say so rather than sit on "Connecting…" looking busy.
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    private readonly Action<bool> _onChanged;

    public NetworkWatcher(Action<bool> onChanged)
    {
        _onChanged = onChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public static bool IsDown => !NetworkInterface.GetIsNetworkAvailable();

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Report();

    private void OnAddressChanged(object? sender, EventArgs e) => Report();

    private void Report()
    {
        var down = IsDown;
        Log.Info("app", down
            ? "the network is down — the camera cannot be reached"
            : "the network is back");
        _onChanged(down);
    }
}

/// <summary>
/// WIN-18: the system's own accessibility and personalisation settings. An app that quietly ignores
/// them is an app that has decided it knows better than the person using it.
/// </summary>
public static class SystemPreferences
{
    private static readonly UISettings Settings = new();

    /// <summary>True when the user has turned transparency effects off — no acrylic, and no fading.</summary>
    public static bool TransparencyDisabled
    {
        get
        {
            try
            {
                return !Settings.AdvancedEffectsEnabled;
            }
            catch (Exception e) when (e is InvalidOperationException or COMException)
            {
                return false;
            }
        }
    }

    /// <summary>True when the user has turned animations off — nothing in the app may animate.</summary>
    public static bool AnimationsDisabled
    {
        get
        {
            try
            {
                return !Settings.AnimationsEnabled;
            }
            catch (Exception e) when (e is InvalidOperationException or COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Fires when the user changes them while the app is running.</summary>
    public static event Action? Changed;

    static SystemPreferences()
    {
        // The event arrives on a background thread; whoever listens marshals to the UI itself.
        Settings.AdvancedEffectsEnabledChanged += (_, _) => Changed?.Invoke();
    }
}
