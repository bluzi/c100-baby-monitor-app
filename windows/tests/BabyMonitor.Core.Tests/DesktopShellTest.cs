using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Shell;
using BabyMonitor.Core.Ui;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The Windows shell's decisions (DESK-9/11/18/24/25). They live in the core, and are tested, for the same
/// reason every other decision does: a rule that is only written in a view is a rule nobody can test,
/// and this one guards the app's first promise — that a warning is never hidden, and silence is never
/// mistaken for a calm baby.
/// </summary>
public class DesktopShellTest
{
    private static readonly MonitorHealth Healthy = new(
        Running: true,
        Status: Statuses.Live,
        ActiveAlarm: null,
        SessionExpired: false,
        SleepOutage: null);

    // --- DESK-11: what "needs attention" means --------------------------------

    [Fact(DisplayName = "DESK-11 a live healthy monitor needs no attention")]
    public void HealthyNeedsNothing()
    {
        Assert.False(DesktopShell.NeedsAttention(Healthy));
    }

    [Fact(DisplayName = "DESK-11 a ringing alarm needs attention")]
    public void RingingNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { ActiveAlarm = "BabyNoise" }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { ActiveAlarm = "FeedDown" }));
    }

    [Fact(DisplayName = "DESK-11 a feed that is not live while monitoring needs attention")]
    public void ADeadFeedNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = Statuses.Connecting }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = "reconnecting in 4s" }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = "error: connection reset" }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = Statuses.MonitorFailed }));
    }

    [Fact(DisplayName = "DESK-11 a stopped monitor needs attention")]
    public void AStoppedMonitorNeedsAttention()
    {
        // The most dangerous quiet state of all: a picture still on screen and nothing watching it.
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Running = false, Status = Statuses.Stopped }));
    }

    [Fact(DisplayName = "DESK-11 an expired session needs attention")]
    public void AnExpiredSessionNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { SessionExpired = true }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = Statuses.SessionExpired }));
    }

    [Fact(DisplayName = "DESK-11 a sleep outage still unread needs attention")]
    public void AnUnreadSleepOutageNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { SleepOutage = "The PC slept for 8 minutes." }));
    }

    // --- DESK-11: the fade itself ---------------------------------------------

    [Fact(DisplayName = "DESK-11 the mini fades only when nothing is wrong and the pointer is away")]
    public void ItFadesWhenAllIsWell()
    {
        Assert.Equal(
            0.4,
            DesktopShell.MiniOpacity(Healthy, hovering: false, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.4));
    }

    [Fact(DisplayName = "DESK-11 the pointer over the mini makes it solid")]
    public void HoveringMakesItSolid()
    {
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(Healthy, hovering: true, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.4));
    }

    [Fact(DisplayName = "DESK-11 a mini that needs attention never fades, however faint the setting")]
    public void AttentionNeverFades()
    {
        var alarming = Healthy with { ActiveAlarm = "BabyNoise" };
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(alarming, hovering: false, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.25));

        var dead = Healthy with { Status = "error: connection reset" };
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(dead, hovering: false, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.25));
    }

    [Fact(DisplayName = "DESK-11 fading can be turned off")]
    public void FadingCanBeTurnedOff()
    {
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(Healthy, hovering: false, fadeEnabled: false, transparencyDisabled: false, idleOpacity: 0.3));
    }

    [Fact(DisplayName = "DESK-18 turning the system's transparency effects off turns the fade off")]
    public void TransparencyOffTurnsTheFadeOff()
    {
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(Healthy, hovering: false, fadeEnabled: true, transparencyDisabled: true, idleOpacity: 0.3));
    }

    [Fact(DisplayName = "DESK-11 the mini can never be set so faint that it cannot be seen")]
    public void ItCanNeverBecomeInvisible()
    {
        // A stored 0 — an old build, a hand-edited settings file, a slider dragged to the floor — must
        // not produce an invisible monitor.
        Assert.Equal(DesktopShell.MiniOpacityMin, DesktopShell.ClampMiniOpacity(0.0));
        Assert.Equal(DesktopShell.MiniOpacityMin, DesktopShell.ClampMiniOpacity(-3.0));
        Assert.Equal(DesktopShell.MiniOpacityMax, DesktopShell.ClampMiniOpacity(2.0));
        Assert.Equal(0.5, DesktopShell.ClampMiniOpacity(0.5));
        Assert.Equal(DesktopShell.MiniOpacityDefault, DesktopShell.ClampMiniOpacity(double.NaN));

        // And the clamp is not something the caller can forget: it is applied on the way out.
        Assert.Equal(
            DesktopShell.MiniOpacityMin,
            DesktopShell.MiniOpacity(Healthy, hovering: false, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.0));
    }

    // --- DESK-28: the mini tile, locked for a game ----------------------------

    [Fact(DisplayName = "DESK-28 a locked mini holds its idle opacity with no pointer near it")]
    public void LockedHoldsIdleOpacity()
    {
        Assert.Equal(
            0.4,
            DesktopShell.LockedMiniOpacity(fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.4));
    }

    [Fact(DisplayName = "DESK-28 a locked mini stays at its idle opacity even while an alarm rings")]
    public void LockedDoesNotBrightenForAttention()
    {
        // The one place a tile does NOT obey DESK-11's "attention forces it opaque": the whole point of
        // the lock is not to seize the screen back from the game. LockedMiniOpacity takes no health at
        // all — a ringing alarm cannot reach it, and the cry is carried by the alarm's audio path
        // (DESK-23) and the status icon (DESK-1) instead. Contrast MiniOpacity, which snaps to 1.0 here.
        var alarming = Healthy with { ActiveAlarm = "BabyNoise" };
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(alarming, hovering: false, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.3));
        Assert.Equal(
            0.3,
            DesktopShell.LockedMiniOpacity(fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.3));
    }

    [Fact(DisplayName = "DESK-28 a locked mini is solid when fading is off or transparency is disabled")]
    public void LockedIsSolidWhenNotFading()
    {
        Assert.Equal(
            1.0,
            DesktopShell.LockedMiniOpacity(fadeEnabled: false, transparencyDisabled: false, idleOpacity: 0.3));
        Assert.Equal(
            1.0,
            DesktopShell.LockedMiniOpacity(fadeEnabled: true, transparencyDisabled: true, idleOpacity: 0.3));
    }

    [Fact(DisplayName = "DESK-28 a locked mini can never be set so faint that it cannot be seen")]
    public void LockedIsNeverInvisible()
    {
        // Click-through does not mean invisible: a stored 0, from an old build or a slider on the floor,
        // must still clamp to the visible minimum.
        Assert.Equal(
            DesktopShell.MiniOpacityMin,
            DesktopShell.LockedMiniOpacity(fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.0));
    }

    // --- DESK-25: a remembered position can never hide the window -------------

    private static readonly ScreenArea[] OneScreen = [new(0, 0, 1920, 1040)];

    [Fact(DisplayName = "DESK-25 a frame that is on the screen is restored")]
    public void AnOrdinaryFrameIsRestorable()
    {
        Assert.True(DesktopShell.FrameIsRestorable(new WindowFrame(410, 170, 1100, 700), OneScreen));
        Assert.True(DesktopShell.FrameIsRestorable(new WindowFrame(1536, 814, 360, 202), OneScreen));
    }

    [Fact(DisplayName = "DESK-25 the frame of a minimised window is never restored")]
    public void AMinimisedFrameIsNotRestorable()
    {
        // Win32 parks a minimised window at (-32000,-32000) and reports a stub size. Remember that and
        // the monitor reopens where nobody can find it — which is exactly what this machine had stored:
        //   "frame.full": "-32000,-32000,160,39"
        // The user saw the mini tile and believed the main window was simply gone.
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(-32000, -32000, 160, 39), OneScreen));
    }

    [Fact(DisplayName = "DESK-25 a frame on a display that is gone is not restored")]
    public void AFrameOnAVanishedDisplayIsNotRestorable()
    {
        // Remembered while docked to a second monitor on the left; that monitor is now unplugged.
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(-1800, 200, 1100, 700), OneScreen));
        // ...and it comes back when the display does.
        ScreenArea[] two = [new(0, 0, 1920, 1040), new(-1920, 0, 1920, 1040)];
        Assert.True(DesktopShell.FrameIsRestorable(new WindowFrame(-1800, 200, 1100, 700), two));
    }

    [Fact(DisplayName = "DESK-25 a frame with only a sliver on screen is not restored")]
    public void ASliverIsNotRestorable()
    {
        // Nudged almost entirely past the bottom-right edge: technically "on" the screen, but there is
        // nothing left to see or grab.
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(1900, 1030, 360, 202), OneScreen));
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(-350, 500, 360, 202), OneScreen));
    }

    [Fact(DisplayName = "DESK-25 a frame with no screens at all is not restored")]
    public void NoScreensMeansNoRestore()
    {
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(410, 170, 1100, 700), []));
    }

    [Fact(DisplayName = "DESK-25 a nonsense stored size is not restored")]
    public void NonsenseSizesAreNotRestorable()
    {
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(100, 100, 0, 0), OneScreen));
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(100, 100, -1100, -700), OneScreen));
        Assert.False(DesktopShell.FrameIsRestorable(new WindowFrame(100, 100, 12, 8), OneScreen));
    }

    // --- DESK-24: a firewall that never says so -------------------------------

    private const string LanSearchFailure = "error: cs2: handshake timeout (LAN search)";

    [Fact(DisplayName = "DESK-24 repeated failure at the first step is called out as a likely firewall")]
    public void RepeatedHandshakeTimeoutsAreSuspicious()
    {
        // The real thing, from this machine's log: the camera answers its punch from an ephemeral port,
        // Windows Firewall calls that unsolicited and drops it, and the monitor reconnects forever —
        // looking busy while seeing nothing. Attempt 13 must not be the first hint.
        var watch = new FirewallSuspicion();
        Assert.False(watch.Suspected);

        watch.Observe(Statuses.Connecting);
        watch.Observe(LanSearchFailure);
        Assert.False(watch.Suspected); // one failure is a blip, not a diagnosis

        watch.Observe("reconnecting in 2s");
        watch.Observe(Statuses.Connecting);
        watch.Observe(LanSearchFailure);
        Assert.True(watch.Suspected);
    }

    [Fact(DisplayName = "DESK-24 a feed that goes live clears the suspicion")]
    public void GoingLiveClearsIt()
    {
        var watch = new FirewallSuspicion();
        watch.Observe(LanSearchFailure);
        watch.Observe(LanSearchFailure);
        Assert.True(watch.Suspected);

        watch.Observe(Statuses.Live);
        Assert.False(watch.Suspected);
    }

    [Fact(DisplayName = "DESK-24 a different failure is never blamed on the firewall")]
    public void OtherFailuresAreNotBlamedOnTheFirewall()
    {
        // A firewall that drops the punch fails at LAN search and nowhere else. Anything that got
        // further — a dropped session, a camera that stopped streaming — is a different problem, and
        // saying "firewall" about it would send the parent to fix the wrong thing.
        var watch = new FirewallSuspicion();
        watch.Observe(LanSearchFailure);
        watch.Observe("error: cs2: connection closed");
        watch.Observe("error: cs2: connection closed");
        Assert.False(watch.Suspected);

        // ...and a later run of real handshake timeouts still counts from scratch.
        watch.Observe(LanSearchFailure);
        Assert.False(watch.Suspected);
        watch.Observe(LanSearchFailure);
        Assert.True(watch.Suspected);
    }

    [Fact(DisplayName = "DESK-24 the countdown between attempts does not count as an attempt")]
    public void TheCountdownIsNotAFailure()
    {
        // The status ticks "reconnecting in 15s… 14s…" once a second between attempts. If those counted,
        // a single timeout would be enough to cry firewall.
        var watch = new FirewallSuspicion();
        watch.Observe(LanSearchFailure);
        for (var s = 15; s > 0; s--)
        {
            watch.Observe($"reconnecting in {s}s");
        }

        Assert.False(watch.Suspected);
    }

    [Fact(DisplayName = "DESK-24 the advice says what to allow")]
    public void TheAdviceIsActionable()
    {
        // It must name the app and the firewall: a warning a half-asleep parent cannot act on is decor.
        Assert.Contains("Firewall", DesktopShell.FirewallAdvice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Baby Monitor", DesktopShell.FirewallAdvice, StringComparison.Ordinal);
        // And it must not state as fact something it only suspects — the camera may simply be off.
        Assert.Contains("likely", DesktopShell.FirewallAdvice, StringComparison.OrdinalIgnoreCase);
    }

    // --- BG-14: a PC has no Stop --------------------------------------------

    [Fact(DisplayName = "BG-14 the PC never offers Stop, however the monitor is doing")]
    public void ThereIsNoStopOnAPc()
    {
        // On a PC the app IS the monitor: it watches until it is exited. So Baby Monitor cannot sit in
        // the tray, alive, over a watch that ended hours ago — the state does not exist, because the
        // control that creates it does not.
        var states = new (bool Running, string Status)[]
        {
            (true, Statuses.Live),
            (true, Statuses.Connecting),
            (true, "reconnecting in 3s"),
            (true, "error: connection reset"),
            (true, Statuses.MonitorFailed),
            (false, Statuses.Stopped),
            (true, Statuses.SessionExpired),
        };

        foreach (var (running, status) in states)
        {
            Assert.DoesNotContain(
                ViewerActionKind.Stop,
                DesktopShell.ViewerActions(running, status));
        }
    }

    [Fact(DisplayName = "BG-14 a monitor that failed on its own can still be started without exiting")]
    public void AFailedMonitorCanStillBeStarted()
    {
        // WATCH-11: `running` stays true when the monitor fails, and that failure has to be recoverable
        // right there — otherwise the only cure for a broken monitor is exiting the app.
        Assert.Contains(ViewerActionKind.Resume, DesktopShell.ViewerActions(true, Statuses.MonitorFailed));
        Assert.Contains(ViewerActionKind.Resume, DesktopShell.ViewerActions(false, Statuses.Stopped));
        Assert.DoesNotContain(ViewerActionKind.Resume, DesktopShell.ViewerActions(true, Statuses.Live));
    }

    [Fact(DisplayName = "BG-14 the PC still gets everything else the phone gets")]
    public void ThePcGetsEverythingElse()
    {
        var live = DesktopShell.ViewerActions(true, Statuses.Live);
        Assert.Contains(ViewerActionKind.Mute, live);
        Assert.Contains(ViewerActionKind.NightVision, live);
        Assert.Contains(ViewerActionKind.Alerts, live);
    }

    // --- DESK-9: which shape the one window is in ----------------------------

    [Fact(DisplayName = "DESK-9 the viewer keeps whichever shape the user chose")]
    public void TheViewerKeepsItsShape()
    {
        Assert.Equal(DesktopShell.ShapeMini, DesktopShell.WindowShape("viewer", DesktopShell.ShapeMini));
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("viewer", DesktopShell.ShapeFull));
    }

    [Fact(DisplayName = "DESK-5 signing in or picking a camera is never done in a tile")]
    public void SignInIsNeverATile()
    {
        // There is no video to float and there are fields to type into: the window goes full.
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("login", DesktopShell.ShapeMini));
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("devices", DesktopShell.ShapeMini));
    }

    [Fact(DisplayName = "DESK-9 an unknown stored shape is full rather than nothing")]
    public void AnUnknownShapeIsFull()
    {
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("viewer", "gibberish"));
    }

    [Fact(DisplayName = "DESK-8 each corner hugs the right edges")]
    public void EachCornerHugsTheRightEdges()
    {
        Assert.True(DesktopShell.MiniCornerHugsRight(DesktopShell.MiniCornerBottomRight));
        Assert.True(DesktopShell.MiniCornerHugsBottom(DesktopShell.MiniCornerBottomRight));

        Assert.False(DesktopShell.MiniCornerHugsRight(DesktopShell.MiniCornerBottomLeft));
        Assert.True(DesktopShell.MiniCornerHugsBottom(DesktopShell.MiniCornerBottomLeft));

        Assert.True(DesktopShell.MiniCornerHugsRight(DesktopShell.MiniCornerTopRight));
        Assert.False(DesktopShell.MiniCornerHugsBottom(DesktopShell.MiniCornerTopRight));

        Assert.False(DesktopShell.MiniCornerHugsRight(DesktopShell.MiniCornerTopLeft));
        Assert.False(DesktopShell.MiniCornerHugsBottom(DesktopShell.MiniCornerTopLeft));
    }

    [Fact(DisplayName = "DESK-8 an unknown stored corner falls back to bottom-right — never off-screen")]
    public void AnUnknownCornerFallsBackToBottomRight()
    {
        Assert.True(DesktopShell.MiniCornerHugsRight("gibberish"));
        Assert.True(DesktopShell.MiniCornerHugsBottom(string.Empty));
    }
}
