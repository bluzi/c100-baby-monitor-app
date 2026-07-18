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
/// is not. (DESK-11 is one criterion for both desktops, so the Mac's shell decides this the same way
/// in its own copy of the core, and both suites run the same cases against it.)
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

    /// <summary>DESK-8: the corners the mini tile can be parked in, and the one it starts in.</summary>
    public const string MiniCornerBottomRight = "bottom-right";
    public const string MiniCornerBottomLeft = "bottom-left";
    public const string MiniCornerTopRight = "top-right";
    public const string MiniCornerTopLeft = "top-left";
    public const string MiniCornerDefault = MiniCornerBottomRight;

    /// <summary>
    /// DESK-8: decode a stored corner into which edges the tile hugs — right, and bottom. Anything
    /// unrecognised falls back to the bottom-right corner, so a corrupt stored value can never strand
    /// the tile off-screen. Which corner is a shared decision; the pixel maths is each desktop's own,
    /// because a Mac's y-axis points up and a PC's points down.
    /// </summary>
    public static bool MiniCornerHugsRight(string corner) =>
        corner is not (MiniCornerBottomLeft or MiniCornerTopLeft);

    public static bool MiniCornerHugsBottom(string corner) =>
        corner is not (MiniCornerTopRight or MiniCornerTopLeft);

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
    /// DESK-28 `[windows]`: how solid the mini tile is drawn **while it is locked** for a game.
    ///
    /// This is the one deliberate departure from <see cref="MiniOpacity"/>: a locked tile is held at its
    /// idle opacity no matter what — it does not brighten for the pointer (it cannot be pointed at; it is
    /// click-through), and it does not brighten for a ringing alarm the way DESK-11 otherwise demands.
    /// The point of the lock is that the tile never seizes the screen back from the game, and the alarm's
    /// own audio path (DESK-23) and the status icon (DESK-1) are what carry the cry instead. The fade
    /// setting is still honoured — turn fading off, or the system's transparency effects, and a locked
    /// tile is simply solid — so "locked" only ever changes whether attention forces it opaque, never
    /// whether the parent gets to see it at all.
    /// </summary>
    public static double LockedMiniOpacity(bool fadeEnabled, bool transparencyDisabled, double idleOpacity)
    {
        if (!fadeEnabled || transparencyDisabled)
        {
            return MiniOpacityMax;
        }

        return ClampMiniOpacity(idleOpacity);
    }

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
    /// DESK-24: what the parent is told when the camera never answers. It names the app and the thing to
    /// allow, because a warning nobody can act on is decor — and it says *likely*, because the same
    /// silence is what an unplugged camera sounds like, and sending someone to fix a firewall that was
    /// never the problem is its own kind of lie.
    /// </summary>
    public const string FirewallAdvice =
        "Can't reach the camera. If it is powered on and on this network, Windows Firewall is likely " +
        "blocking its reply — allow Baby Monitor through Windows Firewall (Private and Public).";

    /// <summary>DESK-24: the short form, for the mini tile — enough that the parent knows to look.</summary>
    public const string FirewallAdviceShort = "Can't reach camera — check firewall";

    /// <summary>
    /// DESK-25: the smallest frame worth restoring, and the smallest patch of one that has to remain on a
    /// screen for the window to count as findable — enough to see and to grab, not a sliver of border.
    /// </summary>
    public const int MinFrameWidth = 160;
    public const int MinFrameHeight = 80;
    public const int MinVisibleWidth = 120;
    public const int MinVisibleHeight = 48;

    /// <summary>
    /// DESK-25: **may a remembered frame be given back to the window?** Only if it would land somewhere the
    /// parent can actually see it.
    ///
    /// Two ways a stored frame turns into a monitor that never appears, and both have happened:
    ///   * the window was **minimised** when its frame was remembered — Win32 parks a minimised window at
    ///     (-32000,-32000) and reports a stub size, so the frame that comes back is off every screen;
    ///   * the display it was last on is **gone** — undocked, unplugged, or rearranged.
    ///
    /// Either way the window opens far outside the desktop, the parent sees nothing, and the monitor looks
    /// dead while it is in fact running. That is the exact failure this project exists to prevent, so the
    /// check is here, in the core, where it is tested — and it is phrased as "restorable or else", so a
    /// case nobody thought of falls back to the default position rather than off the edge of the world.
    /// </summary>
    public static bool FrameIsRestorable(WindowFrame frame, IReadOnlyList<ScreenArea> screens)
    {
        if (frame.Width < MinFrameWidth || frame.Height < MinFrameHeight)
        {
            return false;
        }

        foreach (var screen in screens)
        {
            // How much of the window would actually be on this screen.
            var visibleWidth =
                Math.Min(frame.X + frame.Width, screen.X + screen.Width) - Math.Max(frame.X, screen.X);
            var visibleHeight =
                Math.Min(frame.Y + frame.Height, screen.Y + screen.Height) - Math.Max(frame.Y, screen.Y);

            // A window smaller than the minimum patch only has to be visible in full.
            if (visibleWidth >= Math.Min(MinVisibleWidth, frame.Width) &&
                visibleHeight >= Math.Min(MinVisibleHeight, frame.Height))
            {
                return true;
            }
        }

        return false;
    }

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

/// <summary>
/// DESK-24: **is the PC's own firewall eating the camera's answer?**
///
/// The camera is not reached by connecting to it; it is asked to punch back, and it answers from an
/// ephemeral port of its own. Windows Firewall never sent anything to that port, so on a Public network
/// it drops the reply as unsolicited — and the handshake dies at its very first step, over and over,
/// while the app shows a tidy "reconnecting in 15s" that a parent reads as "it is working on it".
/// A Mac and a phone have no such filter, which is why this class exists only here.
///
/// It watches for that one signature — repeated failure at LAN search, the step that cannot fail for
/// any reason *except* not hearing the camera — and it is deliberately conservative in both directions:
/// one timeout is a blip, not a diagnosis, and a failure that got further is somebody else's fault.
/// </summary>
public sealed class FirewallSuspicion
{
    /// <summary>How many consecutive first-step failures before we say anything. One is a blip.</summary>
    public const int AfterFailures = 2;

    /// <summary>
    /// The failure the firewall produces. Matched on text rather than a type because the protocol layer
    /// is a mirror of the Kotlin core, byte for byte and line for line — a Windows-only exception added
    /// there would be a divergence, and this rule is Windows-only. The string is the message Cs2 throws.
    /// </summary>
    private const string LanSearchTimeout = "handshake timeout (LAN search)";

    private int _failures;

    /// <summary>DESK-24: true once the camera has failed to answer often enough to be worth saying.</summary>
    public bool Suspected { get; private set; }

    /// <summary>Feed every engine status. Anything that is not a verdict is ignored.</summary>
    public void Observe(string status)
    {
        if (status == Statuses.Live)
        {
            Reset(); // it answered; whatever we suspected, we were wrong
            return;
        }

        if (!status.StartsWith("error:", StringComparison.Ordinal))
        {
            return; // "connecting", the countdown between attempts — not a verdict either way
        }

        if (!status.Contains(LanSearchTimeout, StringComparison.Ordinal))
        {
            Reset(); // it got past the first step, so the firewall is not what is wrong
            return;
        }

        _failures++;
        if (_failures >= AfterFailures)
        {
            Suspected = true;
        }
    }

    public void Reset()
    {
        _failures = 0;
        Suspected = false;
    }
}

/// <summary>DESK-25: a window's position and size, in physical pixels, as it was remembered.</summary>
public readonly record struct WindowFrame(int X, int Y, int Width, int Height);

/// <summary>DESK-25: a screen's usable area, in the same physical pixels the frame is in.</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height);

/// <summary>What the shell needs to know to decide whether the monitor can be allowed to fade quietly.</summary>
public sealed record MonitorHealth(
    bool Running,
    string Status,
    string? ActiveAlarm,
    bool SessionExpired,
    string? SleepOutage);
