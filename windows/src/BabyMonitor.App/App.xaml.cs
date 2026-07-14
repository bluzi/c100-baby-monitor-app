using BabyMonitor.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // DESK-6 / the first principle: the app never exits itself — not on an update, not on an error.
        // A XAML page that throws must cost a warning in the log, never the monitor.
        UnhandledException += (_, e) =>
        {
            Log.Error("app", $"unhandled exception: {e.Message}", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("app", "unhandled exception on a background thread", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("app", "an unobserved task failed", e.Exception);
            e.SetObserved();
        };
    }

    public static MainWindow? Window { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();

        // BG-13: a PC that restarted overnight comes back with the monitor stopped. The window opens
        // on the feed with Start right there, so one click fixes it.
        Window.Activate();
    }
}

/// <summary>
/// The entry point, written out rather than generated, because **one thing has to happen before XAML
/// exists**: a staged update (UPD-5) is applied by the *new* build, which the old one starts with
/// `--apply-update`. That process's whole job is to wait, swap the files and relaunch — it must never
/// put a window on screen, and it must never start a monitor it is about to tear down.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Logging.Install();

        if (Updater.TryApplyStagedUpdate(args))
        {
            return; // we were the swap; the installed build is starting now
        }

        // UPD-10's other half, and the one that makes the first half survivable: a version the last
        // run put in place takes over HERE, before a window, an engine or a tray exists — and above
        // all before the monitor connects to a camera it would have to drop again seconds later.
        if (Updater.ApplyStagedAtLaunch())
        {
            return; // the new version is starting; this one's job is done
        }

        Log.Info("app", $"Baby Monitor {Updater.CurrentVersion} starting");

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
