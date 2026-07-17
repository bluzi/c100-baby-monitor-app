using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>WATCH-12: audio can be alive while the picture is a photograph.</summary>
public class PictureLivenessTest
{
    private static byte[] Frame(int seed, int size = 512)
    {
        var b = new byte[size];
        for (var i = 0; i < size; i++)
        {
            b[i] = (byte)(i * 31 + seed);
        }

        return b;
    }

    [Fact(DisplayName = "WATCH-12 a picture that keeps changing is never called frozen")]
    public void AMovingPictureIsNeverFrozen()
    {
        var p = new PictureLiveness();
        long now = 0;
        // A working feed, well past the freeze window: a new picture every 40ms for a minute.
        for (var i = 0; i < 1500; i++)
        {
            p.OnFrame(Frame(i), nowMs: now);
            now += 40;
            Assert.False(p.Frozen(now), $"a moving picture was called frozen at {now}ms");
        }
    }

    [Fact(DisplayName = "WATCH-12 a picture that stops arriving is frozen")]
    public void APictureThatStopsArrivingIsFrozen()
    {
        var p = new PictureLiveness();
        p.OnFrame(Frame(1), nowMs: 0);

        // The stream goes quiet. Nothing arrives, so nothing changes.
        Assert.False(p.Frozen(9_000));
        Assert.True(p.Frozen(10_001));
    }

    [Fact(DisplayName = "WATCH-12 the same picture arriving over and over is frozen")]
    public void ARepeatedPictureIsFrozen()
    {
        // The failure that looks most like health — and the reason the frame's timestamp is not trusted:
        // frames keep coming, the socket is busy, the timeline advances, the feed is honestly live, and
        // every one of them is the same photograph. A stream is only moving if its picture is.
        var p = new PictureLiveness();
        var stuck = Frame(7);
        long now = 0;
        for (var i = 0; i < 300; i++)
        {
            p.OnFrame(stuck, nowMs: now);
            now += 40;
        }

        Assert.True(p.Frozen(now), $"a repeated frame stood for {now}ms and was not called frozen");
    }

    [Fact(DisplayName = "WATCH-12 one changed byte is a moving picture")]
    public void OneChangedByteIsMovement()
    {
        // The picture carries a clock, so a working feed cannot repeat a frame — but the difference
        // between one second and the next may be small. Movement must not need a big diff to count.
        var p = new PictureLiveness();
        var a = Frame(1);
        var b = (byte[])a.Clone();
        b[b.Length / 2]++;
        long now = 0;
        for (var i = 0; i < 300; i++)
        {
            p.OnFrame(i % 2 == 0 ? a : b, nowMs: now);
            now += 40;
            Assert.False(p.Frozen(now));
        }
    }

    [Fact(DisplayName = "WATCH-12 a camera that never sends a picture is not frozen")]
    public void NoPictureAtAllIsNotFrozen()
    {
        // LIVE-7 / DESK-22: no picture is a gap already said out loud, and audio monitoring is what
        // matters. Reconnecting for ever over a picture that was never coming would take the sound down
        // with it — the one thing that must not happen.
        var p = new PictureLiveness();

        Assert.False(p.Frozen(60_000));
    }

    [Fact(DisplayName = "WATCH-12 a new session starts with a clean slate")]
    public void ResetClearsAFrozenPicture()
    {
        var p = new PictureLiveness();
        p.OnFrame(Frame(1), nowMs: 0);
        Assert.True(p.Frozen(20_000));

        p.Reset();

        Assert.False(p.Frozen(20_000), "a reconnected session inherited the last one's frozen picture");
    }

    [Fact(DisplayName = "WATCH-12 the picture is given a few seconds before it is called frozen")]
    public void ABlipIsNotAFreeze()
    {
        // A blip is not a freeze: a feed that skips a beat must not cost a parent their sound.
        var p = new PictureLiveness();
        p.OnFrame(Frame(1), nowMs: 0);

        Assert.False(p.Frozen(1_000));
        Assert.False(p.Frozen(PictureLiveness.FreezeMsDefault));
        Assert.True(p.Frozen(PictureLiveness.FreezeMsDefault + 1));
    }
}
