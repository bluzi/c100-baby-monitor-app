using BabyMonitor.Core.Data;
using BabyMonitor.Core.Dsp;
using BabyMonitor.Core.Monitor;
using Concentus.Enums;
using Concentus.Structs;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>A speaker that records rather than plays — and can be made to fail like a real one.</summary>
internal sealed class FakePcmSink : IPcmSink
{
    public float Volume { get; set; } = 1f;

    public int Started { get; private set; }

    public int Stopped { get; private set; }

    public List<short[]> Written { get; } = new();

    public bool FailOnWrite { get; set; }

    public void Start(int sampleRate) => Started++;

    public void Write(short[] pcm)
    {
        if (FailOnWrite)
        {
            throw new InvalidOperationException("the audio device is gone");
        }

        Written.Add(pcm);
    }

    public void Stop() => Stopped++;

    public void Dispose()
    {
    }
}

public class OpusAudioOutputTest
{
    /// <summary>One second of real Opus packets, encoded the way the camera does (48 kHz mono, 20 ms).</summary>
    private static List<byte[]> OpusPackets(int count = 50)
    {
        var encoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        var packets = new List<byte[]>();
        var pcm = Sounds.BabyCry(960 * count);
        var buffer = new byte[4000];
        for (var i = 0; i < count; i++)
        {
            var frame = pcm[(i * 960)..((i + 1) * 960)];
            var n = encoder.Encode(frame, 0, 960, buffer, 0, buffer.Length);
            packets.Add(buffer[..n]);
        }

        return packets;
    }

    [Fact(DisplayName = "LIVE-1 opus packets from the camera decode into PCM and reach the speaker")]
    public void OpusDecodesToTheSpeaker()
    {
        var sink = new FakePcmSink();
        var output = new OpusAudioOutput(sink, (_, _) => { });
        output.Start();

        foreach (var packet in OpusPackets())
        {
            output.Push(packet, 0);
        }

        Assert.Equal(1, sink.Started);
        Assert.NotEmpty(sink.Written);
        Assert.All(sink.Written, pcm => Assert.Equal(960, pcm.Length));
        output.Release();
        Assert.Equal(1, sink.Stopped);
    }

    [Fact(DisplayName = "LIVE-3 muting silences the speaker only — decoding and the analysis tap carry on")]
    public void MuteSilencesTheSpeakerOnly()
    {
        // The load-bearing contract of the whole audio path. An implementation that muted by pausing
        // the decoder would have quietly disabled the crying alarm: the app would look like it was
        // monitoring, and it would not be.
        var windows = 0;
        var sink = new FakePcmSink();
        var output = new OpusAudioOutput(sink, (_, _) => windows++);
        output.Start();

        output.Muted = true;
        Assert.Equal(0f, sink.Volume); // the speaker, and only the speaker

        foreach (var packet in OpusPackets())
        {
            output.Push(packet, 0);
        }

        Assert.True(windows > 0, "the analysis tap must keep receiving windows while muted");
        Assert.NotEmpty(sink.Written); // still decoding, still writing — at zero volume

        output.Muted = false;
        Assert.Equal(1f, sink.Volume);
        output.Release();
    }

    [Fact(DisplayName = "LIVE-3 the analysis tap sees the room, not silence — a muted feed still alarms")]
    public void TheAnalysisTapSeesTheRoom()
    {
        // Proof the tap carries the real sound: the level meter must rise on it while muted.
        var meter = new LevelMeter();
        var level = 0.0;
        long now = 0;
        var output = new OpusAudioOutput(
            new FakePcmSink(),
            (pcm, rate) =>
            {
                now += pcm.Length * 1000L / rate;
                var metrics = Analysis.AnalyzeWindow(pcm, rate);
                level = meter.Process(metrics.Rms, metrics.Peak, now);
            });
        output.Start();
        output.Muted = true;

        foreach (var packet in OpusPackets(200))
        {
            output.Push(packet, 0);
        }

        Assert.True(level > 0.0, "a muted feed must still measure the room");
        output.Release();
    }

    [Fact(DisplayName = "LIVE-3 a corrupt packet is a blip, not a dead connection")]
    public void ACorruptPacketIsSurvivable()
    {
        var sink = new FakePcmSink();
        var output = new OpusAudioOutput(sink, (_, _) => { });
        output.Start();

        output.Push(new byte[] { 0xff, 0xff, 0xff, 0xff }, 0); // nonsense
        foreach (var packet in OpusPackets(5))
        {
            output.Push(packet, 0); // and the stream carries on
        }

        Assert.NotEmpty(sink.Written);
        output.Release();
    }

    [Fact(DisplayName = "LIVE-1 a dead audio path throws rather than playing silence")]
    public void ADeadSpeakerThrows()
    {
        // A parent must never mistake a broken audio path for a quiet room. The engine treats this as
        // a dead connection and rebuilds it.
        var sink = new FakePcmSink { FailOnWrite = true };
        var output = new OpusAudioOutput(sink, (_, _) => { });
        output.Start();

        Assert.Throws<InvalidOperationException>(() => output.Push(OpusPackets(1)[0], 0));
        output.Release();
    }
}

/// <summary>A decoder that can be told to behave like a PC without the HEVC Video Extensions.</summary>
internal sealed class FakeVideoRenderer : IVideoRenderer
{
    public bool Available { get; set; } = true;

    public bool ThrowOnDecode { get; set; }

    public int Configured { get; private set; }

    public int Decoded { get; private set; }

    public (int Width, int Height) Size { get; private set; }

    public bool TornDown { get; private set; }

    public bool Configure(byte[] vps, byte[] sps, byte[] pps, int width, int height)
    {
        if (!Available)
        {
            return false;
        }

        Configured++;
        Size = (width, height);
        return true;
    }

    public void Decode(byte[] annexB, long ptsMs)
    {
        if (ThrowOnDecode)
        {
            throw new InvalidOperationException("the decoder fell over");
        }

        Decoded++;
    }

    public void TearDown() => TornDown = true;
}

public class HevcVideoOutputTest : IDisposable
{
    private static readonly byte[] Vps = { 0, 0, 0, 1, Hevc.NalVps << 1, 1, 0x0c, 0x01 };
    private static readonly byte[] Pps = { 0, 0, 0, 1, Hevc.NalPps << 1, 1, 0xc1, 0x73 };
    private static readonly byte[] Keyframe = { 0, 0, 0, 1, 19 << 1, 1, 0xaa, 0xbb };
    private static readonly byte[] InterFrame = { 0, 0, 0, 1, 1 << 1, 1, 0xcc, 0xdd };

    /// <summary>A real 1920x1080 SPS, with its start code, as the camera sends it.</summary>
    private static byte[] Sps()
    {
        var sps = typeof(HevcSpsTest)
            .GetMethod("BuildSps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { 1920, 1080, 0 }) as byte[];
        return new byte[] { 0, 0, 0, 1 }.Concat(sps!).ToArray();
    }

    public void Dispose() => VideoSink.Renderer = null;

    [Fact(DisplayName = "LIVE-1 the decoder is configured at the first keyframe, once the parameter sets are known")]
    public void ConfiguresAtTheFirstKeyframe()
    {
        var renderer = new FakeVideoRenderer();
        VideoSink.Renderer = renderer;
        var output = new HevcVideoOutput();

        output.Push(InterFrame, 0); // no parameter sets yet, no keyframe: nothing happens
        Assert.Equal(0, renderer.Configured);

        output.Push(Vps.Concat(Sps()).Concat(Pps).ToArray(), 0); // the config-only access unit
        Assert.Equal(0, renderer.Decoded); // config alone is not a picture

        output.Push(Keyframe, 0);
        Assert.Equal(1, renderer.Configured);
        Assert.Equal(1, renderer.Decoded);

        output.Push(InterFrame, 0);
        Assert.Equal(2, renderer.Decoded);
    }

    [Fact(DisplayName = "WIN-19 the camera's picture size reaches the shell, so the window can take its shape")]
    public void TheSizeReachesTheShell()
    {
        (int W, int H)? seen = null;
        var renderer = new FakeVideoRenderer();
        VideoSink.Renderer = renderer;
        var output = new HevcVideoOutput((w, h) => seen = (w, h));

        output.Push(Vps.Concat(Sps()).Concat(Pps).ToArray(), 0);
        output.Push(Keyframe, 0);

        Assert.Equal((1920, 1080), seen);
        Assert.Equal((1920, 1080), renderer.Size);
    }

    [Fact(DisplayName = "WIN-20 a PC with no H.265 decoder says so — and audio monitoring carries on")]
    public void NoDecoderIsSaidOutLoud()
    {
        // This is the one thing a PC can do less than a Mac. A black rectangle that never explains
        // itself is exactly the kind of silence this app refuses.
        var renderer = new FakeVideoRenderer { Available = false };
        VideoSink.Renderer = renderer;
        var output = new HevcVideoOutput();

        output.Push(Vps.Concat(Sps()).Concat(Pps).ToArray(), 0);
        output.Push(Keyframe, 0);

        Assert.True(output.DecoderUnavailable); // the shell reads this and tells the parent
        Assert.Equal(0, renderer.Decoded);

        // And it never throws, so the audio path — the level meter, the alarm — is untouched.
        output.Push(InterFrame, 0);
        output.Push(Keyframe, 0);
    }

    [Fact(DisplayName = "LIVE-7 a decoder that falls over never throws into the monitor")]
    public void ADeadDecoderNeverThrows()
    {
        var renderer = new FakeVideoRenderer { ThrowOnDecode = true };
        VideoSink.Renderer = renderer;
        var output = new HevcVideoOutput();

        output.Push(Vps.Concat(Sps()).Concat(Pps).ToArray(), 0);
        // Video trouble must never take audio monitoring down with it.
        output.Push(Keyframe, 0);
        output.Push(InterFrame, 0);
    }

    [Fact(DisplayName = "LIVE-7 with no window open the frames go nowhere and nothing breaks")]
    public void NoWindowIsFine()
    {
        VideoSink.Renderer = null;
        var output = new HevcVideoOutput();
        output.Push(Vps.Concat(Sps()).Concat(Pps).ToArray(), 0);
        output.Push(Keyframe, 0);
        output.Push(InterFrame, 0);
    }
}

/// <summary>An alarm voice that records what it was asked to play.</summary>
internal sealed class FakeAlarmVoice : IAlarmVoice
{
    public float Volume { get; set; }

    public short[]? Pcm { get; private set; }

    public bool Looping { get; private set; }

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public void Start(short[] pcm, int sampleRate, bool loop, float volume)
    {
        Pcm = pcm;
        Looping = loop;
        Volume = volume;
        Started = true;
    }

    public void Stop()
    {
    }

    public void Dispose() => Disposed = true;
}

public class ToneRingerTest : IDisposable
{
    private readonly List<FakeAlarmVoice> _voices = new();
    private readonly ToneRinger _ringer;

    public ToneRingerTest()
    {
        MonitorHub.ActiveAlarm.Value = null;
        MonitorHub.ApplySettings(new Settings());
        _ringer = new ToneRinger(() =>
        {
            var voice = new FakeAlarmVoice();
            lock (_voices)
            {
                _voices.Add(voice);
            }

            return voice;
        });
    }

    public void Dispose()
    {
        _ringer.Acknowledge();
        _ringer.Dispose();
        MonitorHub.ActiveAlarm.Value = null;
    }

    [Fact(DisplayName = "ALRM-4 a triggered alarm rings, and keeps ringing until acknowledged")]
    public async Task ItRingsUntilAcknowledged()
    {
        Assert.True(_ringer.Ring(AlarmKind.BabyNoise, "Nursery"));
        Assert.Equal(AlarmKind.BabyNoise, MonitorHub.ActiveAlarm.Value);

        var voice = await WaitForVoice();
        Assert.True(voice.Started);
        Assert.True(voice.Looping); // it never times out on its own

        _ringer.Acknowledge();
        Assert.Null(MonitorHub.ActiveAlarm.Value);
    }

    [Fact(DisplayName = "ALRM-11 each alarm plays its own sound")]
    public async Task EachAlarmHasItsOwnSound()
    {
        MonitorHub.ApplySettings(new Settings
        {
            CryAlarmSound = Settings.SoundSiren,
            FeedAlarmSound = Settings.SoundSoftChime,
        });

        _ringer.Ring(AlarmKind.FeedDown, "Nursery");
        var voice = await WaitForVoice();
        Assert.Equal(AlarmTones.Pcm(Settings.SoundSoftChime), voice.Pcm);
    }

    [Fact(DisplayName = "ALRM-5+WATCH-6 an alarm that cannot sound because another is ringing is refused, not swallowed")]
    public void ASecondAlarmIsRefused()
    {
        // The ringer says no; the caller (the watchdog) must then retry rather than treat the alarm as
        // delivered. An unheard alarm is not an alarm.
        Assert.True(_ringer.Ring(AlarmKind.BabyNoise, "Nursery"));
        Assert.False(_ringer.Ring(AlarmKind.FeedDown, "Nursery"));
        Assert.Equal(AlarmKind.BabyNoise, MonitorHub.ActiveAlarm.Value);

        _ringer.Acknowledge();
        Assert.True(_ringer.Ring(AlarmKind.FeedDown, "Nursery")); // now it can be heard
    }

    [Fact(DisplayName = "ALRM-14 the alarm starts gentle but audible, and climbs to the configured volume")]
    public async Task ItRampsUp()
    {
        MonitorHub.ApplySettings(new Settings { CryAlarmVolume = 1.0 });
        _ringer.Ring(AlarmKind.BabyNoise, "Nursery");

        var voice = await WaitForVoice();
        var first = voice.Volume;
        Assert.True(first is > 0f and < 1f, $"it must start gentle but audible, got {first}");

        // A few ramp steps later it is louder — and it never overshoots the user's choice.
        for (var i = 0; i < 60 && voice.Volume <= first; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(voice.Volume > first, "the alarm must climb");
        Assert.True(voice.Volume <= 1f);
    }

    private async Task<FakeAlarmVoice> WaitForVoice()
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_voices)
            {
                var started = _voices.FirstOrDefault(v => v.Started);
                if (started != null)
                {
                    return started;
                }
            }

            await Task.Delay(20);
        }

        Assert.Fail("the alarm never started playing");
        throw new InvalidOperationException("unreachable");
    }
}
