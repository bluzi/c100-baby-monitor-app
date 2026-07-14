using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

public class StreamWatchdogTest
{
    /// <summary>Tick the dog once per second; did it fire at any tick?</summary>
    private static (bool Fired, long At) Tick(StreamWatchdog dog, bool alive, int seconds, long startMs)
    {
        var fired = false;
        var t = startMs;
        for (var i = 0; i < seconds; i++)
        {
            t += 1000;
            if (dog.OnTick(alive, t))
            {
                fired = true;
            }
        }

        return (fired, t);
    }

    [Fact(DisplayName = "WATCH-1 a disabled watchdog never fires")]
    public void DisabledNeverFires()
    {
        var dog = new StreamWatchdog(enabled: false, graceMs: 5_000);
        var (fired, _) = Tick(dog, alive: false, seconds: 120, startMs: 0);
        Assert.False(fired);
    }

    [Fact(DisplayName = "WATCH-2 fires once after the grace period — whatever the cause")]
    public void FiresOnceAfterGrace()
    {
        var dog = new StreamWatchdog(enabled: true, graceMs: 30_000);
        var (fired, t) = Tick(dog, alive: true, seconds: 10, startMs: 0);
        Assert.False(fired);

        // Feed dies (first observed at t+1000). Quiet through the grace period…
        var early = Tick(dog, alive: false, seconds: 30, startMs: t);
        Assert.False(early.Fired);
        // …fires once the full grace has elapsed since the outage was first seen…
        Assert.True(dog.OnTick(false, early.At + 1000));
        // …and not again for the same outage (WATCH-3).
        var later = Tick(dog, alive: false, seconds: 300, startMs: early.At + 1000);
        Assert.False(later.Fired);
    }

    [Fact(DisplayName = "WATCH-2 the grace period is configurable")]
    public void GraceIsConfigurable()
    {
        var dog = new StreamWatchdog(enabled: true, graceMs: 5_000);
        // Outage first observed at t=1000; fires when 5 s have elapsed since (t=6000).
        var (fired, t) = Tick(dog, alive: false, seconds: 5, startMs: 0);
        Assert.False(fired);
        Assert.True(dog.OnTick(false, t + 1000));
    }

    [Fact(DisplayName = "WATCH-3 recovery re-arms the watchdog for the next outage")]
    public void RecoveryReArms()
    {
        var dog = new StreamWatchdog(enabled: true, graceMs: 5_000);
        var (fired, t) = Tick(dog, alive: false, seconds: 6, startMs: 0);
        Assert.True(fired);

        // Feed comes back, then dies again → a fresh alarm after a fresh grace period.
        t = Tick(dog, alive: true, seconds: 3, startMs: t).At;
        var again = Tick(dog, alive: false, seconds: 6, startMs: t);
        Assert.True(again.Fired);
    }

    [Fact(DisplayName = "WATCH-2 user-stopped monitoring is not an outage")]
    public void StoppingIsNotAnOutage()
    {
        var dog = new StreamWatchdog(enabled: true, graceMs: 5_000);
        Tick(dog, alive: false, seconds: 3, startMs: 0);
        dog.Reset(); // the user pressed Stop mid-outage
        var (fired, _) = Tick(dog, alive: false, seconds: 4, startMs: 3_000);
        Assert.False(fired); // the countdown restarted from the reset
    }

    [Fact(DisplayName = "WATCH-6 an alarm that could not sound is retried — not lost")]
    public void SuppressedAlarmIsRetried()
    {
        var dog = new StreamWatchdog(enabled: true, graceMs: 5_000);
        var (fired, t) = Tick(dog, alive: false, seconds: 6, startMs: 0);
        Assert.True(fired);

        // The ringer was busy with the noise alarm, so nothing actually sounded.
        dog.Unfire();
        // The outage still holds, so the very next tick fires again (no fresh grace period).
        Assert.True(dog.OnTick(false, t + 1000));
        // And it stays a single alarm from there on (WATCH-3).
        var later = Tick(dog, alive: false, seconds: 60, startMs: t + 1000);
        Assert.False(later.Fired);
    }

    [Fact(DisplayName = "WATCH-9 armed only while the crying alarm could itself ring")]
    public void ArmingFollowsTheCryingAlarm()
    {
        var always = new AlarmSchedule(Windowed: false);
        var night = new AlarmSchedule(Windowed: true, StartMinutes: 19 * 60, EndMinutes: 7 * 60);

        Assert.True(StreamWatchdog.Armed(true, true, always, 12 * 60));
        // The crying-alarm toggle off disables the watchdog outright.
        Assert.False(StreamWatchdog.Armed(true, false, always, 12 * 60));
        // Outside the crying alarm's active hours the watchdog is unarmed…
        Assert.False(StreamWatchdog.Armed(true, true, night, 12 * 60));
        // …inside them it is armed (including across midnight).
        Assert.True(StreamWatchdog.Armed(true, true, night, 23 * 60));
        Assert.True(StreamWatchdog.Armed(true, true, night, 3 * 60));
        // The watchdog's own toggle is still required (WATCH-1).
        Assert.False(StreamWatchdog.Armed(false, true, night, 23 * 60));
    }

    [Fact(DisplayName = "WATCH-9 a feed still dead when the watchdog arms alarms then")]
    public void ArmingOverADeadFeedAlarms()
    {
        // Unarmed (crying alarm off / out of hours) while the feed dies: quiet, however long.
        var dog = new StreamWatchdog(enabled: false, graceMs: 5_000);
        var (fired, t) = Tick(dog, alive: false, seconds: 120, startMs: 0);
        Assert.False(fired);

        // The crying alarm turns on / its window opens, feed still dead past grace: alarm now.
        dog.Enabled = true;
        Assert.True(dog.OnTick(false, t + 1000));
        // Still one alarm per outage from here (WATCH-3).
        Assert.False(Tick(dog, alive: false, seconds: 60, startMs: t + 1000).Fired);
    }

    [Fact(DisplayName = "WATCH-9 a new armed window re-alarms an already-acknowledged outage")]
    public void ANewWindowIsANewObligation()
    {
        // Feed dies in the evening window; the alarm fires and is acknowledged.
        var dog = new StreamWatchdog(enabled: true, graceMs: 5_000);
        var (fired, t) = Tick(dog, alive: false, seconds: 6, startMs: 0);
        Assert.True(fired);

        // The window closes overnight-into-day (unarmed), the feed still dead the whole time…
        dog.Enabled = false;
        var quiet = Tick(dog, alive: false, seconds: 600, startMs: t);
        Assert.False(quiet.Fired);

        // …and when the next window opens over the still-dead feed, it alarms again: armed hours must
        // never begin with a dead feed and silence.
        dog.Enabled = true;
        Assert.True(dog.OnTick(false, quiet.At + 1000));
    }

    [Fact(DisplayName = "WATCH-3 within one armed stretch an acknowledged outage stays quiet")]
    public void OneAlarmPerOutage()
    {
        var dog = new StreamWatchdog(enabled: true, graceMs: 5_000);
        var (fired, t) = Tick(dog, alive: false, seconds: 6, startMs: 0);
        Assert.True(fired);
        // Continuously armed: the same outage never fires twice (no arming transition).
        var later = Tick(dog, alive: false, seconds: 600, startMs: t);
        Assert.False(later.Fired);
    }

    [Fact(DisplayName = "WATCH-6 a retried alarm still re-arms normally after recovery")]
    public void RetriedAlarmStillReArms()
    {
        var dog = new StreamWatchdog(enabled: true, graceMs: 5_000);
        var (fired, t) = Tick(dog, alive: false, seconds: 6, startMs: 0);
        Assert.True(fired);
        dog.Unfire();

        // The feed recovers before the retry lands — nothing to alarm about any more.
        t = Tick(dog, alive: true, seconds: 2, startMs: t).At;
        // A brand-new outage still needs a full grace period before it fires.
        var next = Tick(dog, alive: false, seconds: 4, startMs: t);
        Assert.False(next.Fired);
        Assert.True(dog.OnTick(false, next.At + 2000));
    }
}
