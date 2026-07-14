using BabyMonitor.Core.Monitor;

namespace BabyMonitor.Core.Ui;

/// <summary>APP-1: routing is a pure function of what the app already knows.</summary>
public enum Screen
{
    Login,
    Devices,
    Viewer,
}

public static class Router
{
    public static Screen Route(bool hasSession, bool hasDevice) => (hasSession, hasDevice) switch
    {
        (false, _) => Screen.Login,
        (true, false) => Screen.Devices,
        _ => Screen.Viewer,
    };
}

/// <summary>
/// Which controls the live feed offers, and when.
///
/// Shared, so that a button row on a phone, a menu in a Mac's menu bar and a Windows tray menu cannot
/// come to different conclusions about whether monitoring can be stopped or resumed. The *look* of a
/// control is each platform's business; whether it exists is the spec's (BG-11, WATCH-11).
/// </summary>
public enum ViewerActionKind
{
    Resume,
    Stop,
    Mute,
    NightVision,
    Alerts,
}

public static class ViewerActions
{
    public static IReadOnlyList<ViewerActionKind> Kinds(bool running, string status)
    {
        var actions = new List<ViewerActionKind>();

        // APP-3/WATCH-11: a failed monitor keeps `running` true (the watchdog still guards), so it needs
        // its own Resume — the failure must be recoverable right here, not by reopening the app.
        if (!running || status == Statuses.MonitorFailed)
        {
            actions.Add(ViewerActionKind.Resume);
        }

        // BG-11: a running monitor can always be stopped from the feed itself — the tray must not be the
        // only way. A failed monitor is still running, so it offers both.
        if (running)
        {
            actions.Add(ViewerActionKind.Stop);
        }

        actions.Add(ViewerActionKind.Mute);
        actions.Add(ViewerActionKind.NightVision);
        actions.Add(ViewerActionKind.Alerts);
        return actions;
    }
}
