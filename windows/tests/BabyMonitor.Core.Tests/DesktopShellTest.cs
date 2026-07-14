using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Shell;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The Windows shell's decisions (WIN-14/16/18). They live in the core, and are tested, for the same
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

    // --- WIN-16: what "needs attention" means --------------------------------

    [Fact(DisplayName = "WIN-16 a live healthy monitor needs no attention")]
    public void HealthyNeedsNothing()
    {
        Assert.False(DesktopShell.NeedsAttention(Healthy));
    }

    [Fact(DisplayName = "WIN-16 a ringing alarm needs attention")]
    public void RingingNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { ActiveAlarm = "BabyNoise" }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { ActiveAlarm = "FeedDown" }));
    }

    [Fact(DisplayName = "WIN-16 a feed that is not live while monitoring needs attention")]
    public void ADeadFeedNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = Statuses.Connecting }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = "reconnecting in 4s" }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = "error: connection reset" }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = Statuses.MonitorFailed }));
    }

    [Fact(DisplayName = "WIN-16 a stopped monitor needs attention")]
    public void AStoppedMonitorNeedsAttention()
    {
        // The most dangerous quiet state of all: a picture still on screen and nothing watching it.
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Running = false, Status = Statuses.Stopped }));
    }

    [Fact(DisplayName = "WIN-16 an expired session needs attention")]
    public void AnExpiredSessionNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { SessionExpired = true }));
        Assert.True(DesktopShell.NeedsAttention(Healthy with { Status = Statuses.SessionExpired }));
    }

    [Fact(DisplayName = "WIN-16 a sleep outage still unread needs attention")]
    public void AnUnreadSleepOutageNeedsAttention()
    {
        Assert.True(DesktopShell.NeedsAttention(Healthy with { SleepOutage = "The PC slept for 8 minutes." }));
    }

    // --- WIN-16: the fade itself ---------------------------------------------

    [Fact(DisplayName = "WIN-16 the mini fades only when nothing is wrong and the pointer is away")]
    public void ItFadesWhenAllIsWell()
    {
        Assert.Equal(
            0.4,
            DesktopShell.MiniOpacity(Healthy, hovering: false, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.4));
    }

    [Fact(DisplayName = "WIN-16 the pointer over the mini makes it solid")]
    public void HoveringMakesItSolid()
    {
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(Healthy, hovering: true, fadeEnabled: true, transparencyDisabled: false, idleOpacity: 0.4));
    }

    [Fact(DisplayName = "WIN-16 a mini that needs attention never fades, however faint the setting")]
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

    [Fact(DisplayName = "WIN-16 fading can be turned off")]
    public void FadingCanBeTurnedOff()
    {
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(Healthy, hovering: false, fadeEnabled: false, transparencyDisabled: false, idleOpacity: 0.3));
    }

    [Fact(DisplayName = "WIN-18 turning the system's transparency effects off turns the fade off")]
    public void TransparencyOffTurnsTheFadeOff()
    {
        Assert.Equal(
            1.0,
            DesktopShell.MiniOpacity(Healthy, hovering: false, fadeEnabled: true, transparencyDisabled: true, idleOpacity: 0.3));
    }

    [Fact(DisplayName = "WIN-16 the mini can never be set so faint that it cannot be seen")]
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

    // --- WIN-14: which shape the one window is in ----------------------------

    [Fact(DisplayName = "WIN-14 the viewer keeps whichever shape the user chose")]
    public void TheViewerKeepsItsShape()
    {
        Assert.Equal(DesktopShell.ShapeMini, DesktopShell.WindowShape("viewer", DesktopShell.ShapeMini));
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("viewer", DesktopShell.ShapeFull));
    }

    [Fact(DisplayName = "WIN-14 signing in or picking a camera is never done in a tile")]
    public void SignInIsNeverATile()
    {
        // There is no video to float and there are fields to type into: the window goes full.
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("login", DesktopShell.ShapeMini));
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("devices", DesktopShell.ShapeMini));
    }

    [Fact(DisplayName = "WIN-14 an unknown stored shape is full rather than nothing")]
    public void AnUnknownShapeIsFull()
    {
        Assert.Equal(DesktopShell.ShapeFull, DesktopShell.WindowShape("viewer", "gibberish"));
    }
}
