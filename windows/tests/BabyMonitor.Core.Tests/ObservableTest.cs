using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The one value everything else reports *through*.
///
/// Kotlin's core says all of this with a `MutableStateFlow`, which cannot deliver a collector a value
/// older than one it has already seen and cannot let a collector's exception reach the producer. The
/// C# port had to build both properties by hand, so they are pinned by hand.
///
/// This is not a theoretical race. `Status` is written from four threads on an ordinary bad night —
/// the reader saying `live`, the watchdog saying the camera went quiet, the reconnect loop counting
/// down, the failure announcer — and they collide precisely when something is going wrong.
/// </summary>
public class ObservableTest
{
    [Fact(DisplayName = "DESK-1 a subscriber is never left holding a status the monitor has moved on from")]
    public void TheLastNotificationIsAlwaysTheCurrentValue()
    {
        // Two writers racing, over and over. Whatever the interleaving, the final thing the tray was
        // told must be what the box actually holds — otherwise the icon sits quietly on "live" over a
        // feed that died, which is the failure this whole project is built against.
        for (var round = 0; round < 2_000; round++)
        {
            var status = new Observable<string>("connecting");
            var seen = "connecting";
            status.Changed += v => Volatile.Write(ref seen, v);

            var live = new Thread(() => status.Value = "live");
            var dead = new Thread(() => status.Value = "error: the camera stopped sending audio");

            live.Start();
            dead.Start();
            live.Join();
            dead.Join();

            Assert.Equal(status.Value, Volatile.Read(ref seen));
        }
    }

    [Fact(DisplayName = "LIVE-4 an observer that throws does not take the monitor down with it")]
    public void AThrowingObserverCannotEndTheWatch()
    {
        // Handlers run on whichever monitor thread did the write — the audio pump, the reconnect loop.
        // An exception let through here would fault that task and tear down the connection, ~20 times
        // a second. The watch does not end because a view could not set a label.
        var level = new Observable<int>(0);
        var reached = 0;

        level.Changed += _ => throw new InvalidOperationException("a view blew up");
        level.Changed += _ => reached++;

        level.Value = 7;   // must not throw into the caller
        level.Value = 8;

        Assert.Equal(8, level.Value);
        Assert.Equal(2, reached); // and the observers behind the bad one still heard it
    }

    [Fact(DisplayName = "LIVE-6 setting a value to what it already is notifies nobody")]
    public void AnUnchangedValueIsSilent()
    {
        var level = new Observable<int>(3);
        var notifications = 0;
        level.Changed += _ => notifications++;

        level.Value = 3;
        level.Value = 3;
        Assert.Equal(0, notifications);

        level.Value = 4;
        Assert.Equal(1, notifications);
    }
}
