using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App.Services;

/// <summary>
/// One monitor per machine, and no more.
///
/// The app lives in the tray with its window closed (DESK-13/DESK-14 make that the normal resting
/// state), so the obvious thing a parent does — clicking the Start-menu shortcut because they cannot
/// see a window — starts a *second* process. Without a guard that means two tray icons, two engines
/// on one camera, doubled alarm audio, and two stores racing on `settings.json`. Worse: the second
/// process runs the launch-time update swap (UPD-10) and would copy files out from under the first,
/// which is still running from the install directory.
///
/// So a second instance does not run. It pokes the first — which raises its window, the right answer
/// for a tray app — and exits.
/// </summary>
public static class SingleInstance
{
    // Per-user (Local\), not global: two people fast-user-switched into the same PC each get their own
    // monitor, their own session, their own tray icon.
    private const string MutexName = @"Local\BabyMonitor.instance";
    private const string ActivateEventName = @"Local\BabyMonitor.activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;

    // Written on the UI thread (OnActivated), read on the listener thread. volatile so the listener on
    // ARM64 sees the registration rather than a stale null — the worst case is only a dropped
    // "show the window" during the startup gap, but airtight is cheap.
    private static volatile Action? _onActivate;

    /// <summary>
    /// True if we are the first instance and may run. False if another instance already holds the
    /// machine — in which case this call has already asked it to show its window, and the caller must
    /// exit immediately without starting a monitor.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (isFirst)
        {
            StartActivationListener();
            return true;
        }

        // Someone is already monitoring. Bring their window forward and step aside.
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn("app", $"could not signal the running instance: {e.Message}");
        }

        Log.Info("app", "another Baby Monitor is already running — asked it to show, and exiting");
        return false;
    }

    /// <summary>
    /// What to do when a second launch asks us to come forward. The shell registers its window-show
    /// here; it is invoked off the UI thread, so the handler must marshal onto the dispatcher itself.
    /// </summary>
    public static void OnActivated(Action show) => _onActivate = show;

    private static void StartActivationListener()
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                _activate.WaitOne();
                _onActivate?.Invoke();
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-activation",
        };
        thread.Start();
    }
}
