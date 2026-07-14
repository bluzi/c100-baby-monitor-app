using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Shell;
using BabyMonitor.Core.Ui;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The Windows shell's decisions (DESK-9/11/18). They live in the core, and are tested, for the same
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

    [Fact(DisplayName = "DESK-9 signing in or picking a camera is never done in a tile")]
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
}
