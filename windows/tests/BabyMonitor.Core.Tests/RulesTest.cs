using BabyMonitor.Core.Data;
using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Ui;
using Xunit;

namespace BabyMonitor.Core.Tests;

public class AlarmScheduleTest
{
    private static int Min(int h, int m = 0) => (h * 60) + m;

    [Fact(DisplayName = "ALRM-7 always mode is armed at any time of day")]
    public void AlwaysIsAlwaysArmed()
    {
        var schedule = new AlarmSchedule(Windowed: false);
        foreach (var t in new[] { 0, Min(3), Min(12), Min(19, 30), Min(23, 59) })
        {
            Assert.True(schedule.IsActive(t));
        }
    }

    [Fact(DisplayName = "ALRM-7 a same-day window arms only inside it")]
    public void SameDayWindow()
    {
        var schedule = new AlarmSchedule(Windowed: true, StartMinutes: Min(8), EndMinutes: Min(17));
        Assert.True(schedule.IsActive(Min(8)));
        Assert.True(schedule.IsActive(Min(12)));
        Assert.True(schedule.IsActive(Min(16, 59)));
        Assert.False(schedule.IsActive(Min(7, 59)));
        Assert.False(schedule.IsActive(Min(17)));
        Assert.False(schedule.IsActive(Min(23)));
    }

    [Fact(DisplayName = "ALRM-7 a window crossing midnight arms through the night")]
    public void MidnightCrossingWindow()
    {
        var schedule = new AlarmSchedule(Windowed: true, StartMinutes: Min(19), EndMinutes: Min(7));
        Assert.True(schedule.IsActive(Min(19)));
        Assert.True(schedule.IsActive(Min(23, 59)));
        Assert.True(schedule.IsActive(0));
        Assert.True(schedule.IsActive(Min(6, 59)));
        Assert.False(schedule.IsActive(Min(7)));
        Assert.False(schedule.IsActive(Min(12)));
        Assert.False(schedule.IsActive(Min(18, 59)));
    }

    [Fact(DisplayName = "ALRM-7 start equal to end means always")]
    public void DegenerateWindowIsAlways()
    {
        var schedule = new AlarmSchedule(Windowed: true, StartMinutes: Min(9), EndMinutes: Min(9));
        Assert.True(schedule.IsActive(Min(9)));
        Assert.True(schedule.IsActive(Min(21)));
    }

    [Fact(DisplayName = "ALRM-7 the schedule derives from settings")]
    public void FromSettings()
    {
        var always = AlarmSchedule.From(new Settings { AlarmScheduleMode = Settings.ScheduleAlways });
        Assert.True(always.IsActive(Min(12)));

        var windowed = AlarmSchedule.From(new Settings
        {
            AlarmScheduleMode = Settings.ScheduleWindow,
            AlarmWindowStartMinutes = Min(19),
            AlarmWindowEndMinutes = Min(7),
        });
        Assert.True(windowed.IsActive(Min(22)));
        Assert.False(windowed.IsActive(Min(12)));
    }
}

public class CatchupTest
{
    [Fact(DisplayName = "LIVE-8 frames flow normally while the consumer keeps up")]
    public void FramesFlowWhenKeepingUp()
    {
        var catchup = new VideoCatchup(maxBacklogFrames: 30);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(catchup.Admit(isKeyframe: i % 40 == 0, backlogFrames: 2));
        }
    }

    [Fact(DisplayName = "LIVE-8 a backlog drops frames until the next keyframe, then resumes")]
    public void BacklogDropsUntilKeyframe()
    {
        var catchup = new VideoCatchup(maxBacklogFrames: 30);
        Assert.True(catchup.Admit(isKeyframe: true, backlogFrames: 0));

        // The decoder stalls; the backlog grows past the limit → non-key frames are dropped…
        Assert.False(catchup.Admit(isKeyframe: false, backlogFrames: 45));
        Assert.False(catchup.Admit(isKeyframe: false, backlogFrames: 44));
        // …even after the backlog drains, until a clean entry point arrives…
        Assert.False(catchup.Admit(isKeyframe: false, backlogFrames: 3));
        // …the next keyframe re-enters and playback resumes.
        Assert.True(catchup.Admit(isKeyframe: true, backlogFrames: 2));
        Assert.True(catchup.Admit(isKeyframe: false, backlogFrames: 1));
    }

    [Fact(DisplayName = "LIVE-8 a keyframe that itself trips the limit is still shown")]
    public void KeyframeAtTheLimitIsShown()
    {
        var catchup = new VideoCatchup(maxBacklogFrames: 30);
        Assert.True(catchup.Admit(isKeyframe: true, backlogFrames: 50));
        Assert.True(catchup.Admit(isKeyframe: false, backlogFrames: 0));
    }
}

// The wiring between "what the engine reports" and "is the feed actually alive" — the decision the
// watchdog (WATCH-2) and the stall-reconnect (WATCH-7) hang on.
public class FeedLivenessTest
{
    [Fact(DisplayName = "WATCH-2 the feed is alive only while live and audio is still arriving")]
    public void AliveOnlyWithFreshAudio()
    {
        const long now = 100_000L;
        Assert.True(FeedLiveness.FeedAlive("live", now - 500, now));
        Assert.False(FeedLiveness.FeedAlive("live", now - 10_000, now)); // gone quiet
        Assert.False(FeedLiveness.FeedAlive("connecting", now, now));
        Assert.False(FeedLiveness.FeedAlive("reconnecting in 5s", now, now));
        Assert.False(FeedLiveness.FeedAlive("error: boom", now, now));
        Assert.False(FeedLiveness.FeedAlive("stopped", now, now));
        Assert.False(FeedLiveness.FeedAlive("live", 0, now)); // no audio ever
    }

    [Fact(DisplayName = "WATCH-7 a live connection that stops delivering audio is stalled and must be dropped")]
    public void StalledIsDropped()
    {
        const long now = 100_000L;
        Assert.False(FeedLiveness.FeedStalled("live", now - 1_000, now));
        Assert.True(FeedLiveness.FeedStalled("live", now - FeedLiveness.StallMs - 1, now));
        // Only a *live* connection can stall; reconnect states are already handled by the loop.
        Assert.False(FeedLiveness.FeedStalled("connecting", 0, now));
        Assert.False(FeedLiveness.FeedStalled("reconnecting in 5s", 0, now));
    }

    [Fact(DisplayName = "WATCH-7 a stalled feed is never reported as alive")]
    public void StalledIsNeverAlive()
    {
        const long now = 100_000L;
        Assert.True(FeedLiveness.FeedStalled("live", now - FeedLiveness.StallMs - 1, now));
        Assert.False(FeedLiveness.FeedAlive("live", now - FeedLiveness.StallMs - 1, now));
    }
}

public class StatusTextTest
{
    [Fact(DisplayName = "LIVE-4 the reconnect status shows whole seconds and counts down")]
    public void ReconnectCountdown()
    {
        Assert.Equal("reconnecting in 5s", Statuses.ReconnectStatus(5000));
        Assert.Equal("reconnecting in 5s", Statuses.ReconnectStatus(4001)); // rounds up, never "0s early"
        Assert.Equal("reconnecting in 1s", Statuses.ReconnectStatus(1000));
        Assert.Equal("reconnecting in 1s", Statuses.ReconnectStatus(1));
    }

    [Fact(DisplayName = "APP-3 raw engine statuses map to readable copy")]
    public void FriendlyStatuses()
    {
        Assert.Equal("Starting…", Statuses.FriendlyStatus("idle"));
        Assert.Equal("Connecting…", Statuses.FriendlyStatus("connecting"));
        Assert.Equal("Live", Statuses.FriendlyStatus("live"));
        Assert.Equal("Stopped", Statuses.FriendlyStatus("stopped"));
        Assert.Equal("Reconnecting in 3s", Statuses.FriendlyStatus("reconnecting in 3s"));
        Assert.Equal("Connection lost — retrying", Statuses.FriendlyStatus("error: Connection reset by peer"));
    }

    [Fact(DisplayName = "LIVE-2 the status line says muted while muted — the icon is never the only clue")]
    public void MutedIsSaidInWords()
    {
        Assert.Equal("Nursery — Live", Statuses.StatusLine("Nursery", Statuses.Live, muted: false));
        Assert.Equal("Nursery — Live · muted", Statuses.StatusLine("Nursery", Statuses.Live, muted: true));
        // Muted is worth saying whatever the connection is doing.
        Assert.Equal("Camera — Connecting… · muted", Statuses.StatusLine("", Statuses.Connecting, muted: true));
    }

    [Fact(DisplayName = "BG-8 an expired session is never dressed up as a retryable connection error")]
    public void ExpiredSessionIsSaidPlainly()
    {
        Assert.Equal(
            "Session expired — open the app to sign in",
            Statuses.FriendlyStatus(Statuses.SessionExpired));
    }

    [Fact(DisplayName = "WATCH-11 a failed monitor says it stopped working — never retrying")]
    public void FailedMonitorSaysSo()
    {
        Assert.Equal(
            "Monitoring stopped working — press Resume to restart",
            Statuses.FriendlyStatus(Statuses.MonitorFailed));
    }

    [Fact(DisplayName = "LIVE-12 an unsupported camera is not dressed up as a connection problem")]
    public void UnsupportedCameraSaysSo()
    {
        Assert.Equal("This camera model isn't supported", Statuses.FriendlyStatus(Statuses.UnsupportedCamera));
    }

    [Fact(DisplayName = "LIVE-6 the room-level line is locale-independent")]
    public void OneDecimalIsLocaleIndependent()
    {
        Assert.Equal("0.0", Format.OneDecimal(0.0));
        Assert.Equal("14.7", Format.OneDecimal(14.65));
        Assert.Equal("-2.5", Format.OneDecimal(-2.45));
        Assert.Equal("NaN", Format.OneDecimal(double.NaN));
    }
}

public class RouterAndBackoffTest
{
    [Fact(DisplayName = "APP-1+AUTH-1 no session routes to sign-in before anything else")]
    public void NoSessionGoesToLogin()
    {
        Assert.Equal(Screen.Login, Router.Route(hasSession: false, hasDevice: false));
        Assert.Equal(Screen.Login, Router.Route(hasSession: false, hasDevice: true));
    }

    [Fact(DisplayName = "APP-1+CAM-1 a session without a camera routes to the picker")]
    public void SessionWithoutCameraGoesToThePicker()
    {
        Assert.Equal(Screen.Devices, Router.Route(hasSession: true, hasDevice: false));
    }

    [Fact(DisplayName = "APP-1+APP-2+CAM-3 session plus stored camera goes straight to the live feed")]
    public void SessionAndCameraGoStraightToTheFeed()
    {
        Assert.Equal(Screen.Viewer, Router.Route(hasSession: true, hasDevice: true));
    }

    [Fact(DisplayName = "LIVE-5 reconnect waits start under a second and grow to a capped maximum")]
    public void BackoffGrowsAndCaps()
    {
        Assert.True(Backoff.ReconnectDelayMs(0) < 1000);
        for (var i = 1; i < Backoff.ReconnectBackoffMs.Count; i++)
        {
            Assert.True(Backoff.ReconnectDelayMs(i) > Backoff.ReconnectDelayMs(i - 1));
        }

        var cap = Backoff.ReconnectBackoffMs[^1];
        Assert.Equal(cap, Backoff.ReconnectDelayMs(100)); // capped at tens of seconds
        Assert.InRange(cap, 10_000, 60_000);
        // A successful connection resets the schedule to the first wait.
        Assert.Equal(Backoff.ReconnectBackoffMs[0], Backoff.ReconnectDelayMs(0));
    }
}

public class ViewerActionsTest
{
    [Fact(DisplayName = "APP-3+WATCH-11 a failed monitor offers Resume right on the live feed — never a dead end")]
    public void FailedMonitorOffersResume()
    {
        // A failed monitor keeps running=true (the watchdog still guards), so Resume must key off the
        // status too — "reopen the app" is not a recovery a half-asleep parent should need.
        Assert.Contains(ViewerActionKind.Resume, ViewerActions.Kinds(true, Statuses.MonitorFailed));
        Assert.Contains(ViewerActionKind.Resume, ViewerActions.Kinds(false, Statuses.Stopped));
        Assert.DoesNotContain(ViewerActionKind.Resume, ViewerActions.Kinds(true, Statuses.Live));
    }

    [Fact(DisplayName = "BG-11 the live feed offers Stop while monitoring runs — and Resume instead once stopped")]
    public void StopIsAlwaysOnTheFeed()
    {
        Assert.Contains(ViewerActionKind.Stop, ViewerActions.Kinds(true, Statuses.Live));
        Assert.DoesNotContain(ViewerActionKind.Stop, ViewerActions.Kinds(false, Statuses.Stopped));
        // A failed monitor is still running (the watchdog guards), so it can be resumed OR stopped
        // outright — both controls show.
        Assert.Contains(ViewerActionKind.Stop, ViewerActions.Kinds(true, Statuses.MonitorFailed));
    }

    [Fact(DisplayName = "LIVE-2+LIVE-9w mute, night vision and alerts stay reachable whatever the monitor is doing")]
    public void TheRestAlwaysShow()
    {
        foreach (var running in new[] { true, false })
        {
            var kinds = ViewerActions.Kinds(running, running ? Statuses.Live : Statuses.Stopped);
            Assert.Contains(ViewerActionKind.Mute, kinds);
            Assert.Contains(ViewerActionKind.NightVision, kinds);
            Assert.Contains(ViewerActionKind.Alerts, kinds);
        }
    }
}
