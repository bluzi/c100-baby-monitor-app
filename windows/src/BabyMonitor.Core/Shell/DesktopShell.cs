using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Ui;

namespace BabyMonitor.Core.Shell;

/// <summary>
/// The Windows shell's decisions — the ones that could hide a warning if they were wrong.
///
/// They are here rather than in a view for the reason every decision in this project is in the core: a
/// rule written in a view is a rule nobody can test, and the rule below ("the floating tile may only
/// fade when there is genuinely nothing to see") guards the app's first promise. Get it wrong and a
/// parent glances at a dimmed picture of a sleeping baby while the feed has been dead for an hour.
///
/// The *look* of the tile is XAML's business. Whether it is allowed to disappear into the background
/// is not. (The Mac's shell has the same rules, in its own copy of the core — DESK-11 mirrors DESK-11
/// deliberately: same hazard, same answer.)
/// </summary>
public static class DesktopShell
{
    /// <summary>DESK-11: faint enough to see through, never so faint it cannot be seen.</summary>
    public const double MiniOpacityMin = 0.25;
    public const double MiniOpacityMax = 1.0;
    public const double MiniOpacityDefault = 0.55;

    /// <summary>DESK-8/9: the two shapes of the one window.</summary>
    public const string ShapeFull = "full";
    public const string ShapeMini = "mini";

    /// <summary>
    /// DESK-11. **Attention is the default.** The tile is allowed to fade only when the monitor is doing
    /// exactly what the parent believes it is doing: running, live, no alarm, no expired session, no
    /// unread sleep outage. Everything else — including a monitor that is merely *stopped*, which is the
    /// quietest failure there is — holds it at full opacity.
    ///
    /// Written as "healthy or else", never as a list of bad states: a status we forgot to enumerate must
    /// fail towards being seen.
    /// </summary>
    public static bool NeedsAttention(MonitorHealth health) => !(
        health.Running &&
        health.Status == Statuses.Live &&
        health.ActiveAlarm == null &&
        !health.SessionExpired &&
        health.SleepOutage == null);

    /// <summary>
    /// DESK-11 / DESK-18: how solid the mini window is drawn right now.
    ///
    /// The clamp is applied here, on the way out, so no caller can forget it and no stored value can
    /// produce an invisible monitor.
    /// </summary>
    public static double MiniOpacity(
        MonitorHealth health,
        bool hovering,
        bool fadeEnabled,
        bool transparencyDisabled,
        double idleOpacity)
    {
        if (hovering || !fadeEnabled || transparencyDisabled)
        {
            return MiniOpacityMax;
        }

        return NeedsAttention(health) ? MiniOpacityMax : ClampMiniOpacity(idleOpacity);
    }

    public static double ClampMiniOpacity(double value) =>
        double.IsNaN(value) ? MiniOpacityDefault : Math.Clamp(value, MiniOpacityMin, MiniOpacityMax);

    /// <summary>
    /// BG-14: **which controls the PC's feed offers — and Stop is never one of them.**
    ///
    /// On a PC the app *is* the monitor: it watches from the moment it opens until it is exited, so
    /// there is no such thing as Baby Monitor running and not monitoring. That state — an app sitting
    /// in the tray looking alive over a watch that ended hours ago — is the quietest failure this
    /// project could ship, and removing the control removes the state.
    ///
    /// Start survives, because a monitor that failed *on its own* (WATCH-11) must be recoverable
    /// without exiting the app.
    ///
    /// It is derived from the shared decision rather than written out fresh, so a control added for
    /// the phone tomorrow appears here too — and it is tested, so nobody can quietly hand a PC a Stop
    /// button back.
    /// </summary>
    public static IReadOnlyList<ViewerActionKind> ViewerActions(bool running, string status) =>
        Ui.ViewerActions.Kinds(running, status)
            .Where(kind => kind != ViewerActionKind.Stop)
            .ToList();

    /// <summary>
    /// DESK-9: which shape the window may take. The mini shape is a *view of a feed* — there is nothing
    /// to float before a camera is chosen, and a sign-in form does not belong in a tile the size of a
    /// postage stamp. So sign-in and the camera picker force the window full, whatever shape the user
    /// last left it in; the shape itself is remembered and comes back with the feed.
    /// </summary>
    public static string WindowShape(string screen, string preferred)
    {
        if (screen != "viewer")
        {
            return ShapeFull;
        }

        return preferred == ShapeMini ? ShapeMini : ShapeFull;
    }
}

/// <summary>What the shell needs to know to decide whether the monitor can be allowed to fade quietly.</summary>
public sealed record MonitorHealth(
    bool Running,
    string Status,
    string? ActiveAlarm,
    bool SessionExpired,
    string? SleepOutage);
