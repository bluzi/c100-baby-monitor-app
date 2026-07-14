using System.Text;
using BabyMonitor.Core.Json;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

public class MissTest
{
    [Fact(DisplayName = "PROTO-20 auth payload carries public key, sign, empty uuid and no encryption")]
    public void AuthPayload()
    {
        var json = new JsonObj(Miss.AuthPayload("aabbcc", "SIGNTOKEN"));
        Assert.Equal("aabbcc", json.GetString("public_key"));
        Assert.Equal("SIGNTOKEN", json.GetString("sign"));
        Assert.Equal(string.Empty, json.GetString("uuid"));
        Assert.Equal(0, json.GetInt("support_encrypt"));
    }

    [Fact(DisplayName = "PROTO-21 quality maps hd to 2 for the C100 and 3 for the C200/C300")]
    public void QualityMapping()
    {
        Assert.Equal(
            """{"videoquality":2,"enableaudio":1}""",
            Miss.StartMediaBody("chuangmi.camera.077ac1", "hd", true));
        Assert.Equal("""{"videoquality":3,"enableaudio":1}""", Miss.StartMediaBody(Miss.ModelC200, "hd", true));
        Assert.Equal("""{"videoquality":3,"enableaudio":1}""", Miss.StartMediaBody(Miss.ModelC300, "hd", true));
        Assert.Equal(
            """{"videoquality":1,"enableaudio":0}""",
            Miss.StartMediaBody("chuangmi.camera.077ac1", "sd", false));
        Assert.Equal(
            """{"videoquality":0,"enableaudio":1}""",
            Miss.StartMediaBody("chuangmi.camera.077ac1", "auto", true));
    }

    [Fact(DisplayName = "PROTO-21 inner commands are BE u32 command plus body")]
    public void InnerCommands()
    {
        var payload = Miss.InnerCommand(Miss.CmdVideoStart, "{}");
        Assert.Equal(0x102L, payload.BeU32(0));
        Assert.Equal("{}", Encoding.UTF8.GetString(payload.Slice(4, payload.Length)));

        var stop = Miss.InnerCommand(Miss.CmdVideoStop);
        Assert.Equal(4, stop.Length);
        Assert.Equal(0x103L, stop.BeU32(0));
    }

    [Fact(DisplayName = "PROTO-22 media headers parse little-endian size, codec, seq, flags and a u64 pts")]
    public void MediaHeaders()
    {
        var hdr = new byte[32];
        hdr.PutLeU32(0, 512);
        hdr.PutLeU32(4, Miss.CodecH265);
        hdr.PutLeU32(8, 42);
        hdr.PutLeU32(12, 0b1000);
        hdr.PutLeU32(16, 5_000_000_000L & 0xffffffffL);
        hdr.PutLeU32(20, 5_000_000_000L >> 32); // > u32 to prove a 64-bit pts

        var parsed = Miss.ParseMediaHeader(hdr);
        Assert.Equal(512L, parsed.Size);
        Assert.Equal(Miss.CodecH265, parsed.CodecId);
        Assert.Equal(42L, parsed.Sequence);
        Assert.Equal(0b1000L, parsed.Flags);
        Assert.Equal(5_000_000_000L, parsed.PtsMs);
    }

    [Fact(DisplayName = "PROTO-23 codec ids map to frames and unknown ids are skipped")]
    public void CodecMapping()
    {
        var data = new byte[] { 1, 2, 3 };
        static Miss.MediaHeader Header(long codec, long flags = 0) =>
            new(Size: 3, CodecId: codec, Sequence: 1, Flags: flags, PtsMs: 10);

        var h265 = Assert.IsType<Frame.Video>(Miss.FrameFromPacket(Header(Miss.CodecH265), data));
        Assert.Equal("h265", h265.Codec);
        Assert.Equal(data, h265.Data);

        var h264 = Assert.IsType<Frame.Video>(Miss.FrameFromPacket(Header(Miss.CodecH264), data));
        Assert.Equal("h264", h264.Codec);

        var opus = Assert.IsType<Frame.Audio>(Miss.FrameFromPacket(Header(Miss.CodecOpus), data));
        Assert.Equal("opus", opus.Codec);
        Assert.Equal(48000, opus.SampleRate);

        var pcma = Assert.IsType<Frame.Audio>(Miss.FrameFromPacket(Header(Miss.CodecPcma), data));
        Assert.Equal("pcma", pcma.Codec);

        Assert.Null(Miss.FrameFromPacket(Header(9999), data));
    }

    [Fact(DisplayName = "PROTO-23 pcm sample rate derives from flags bits 3-6")]
    public void PcmSampleRate()
    {
        Assert.Equal(8000, Miss.PcmSampleRate(0));
        Assert.Equal(16000, Miss.PcmSampleRate(0b1000));
        Assert.Equal(16000, Miss.PcmSampleRate(0b1110000));
        Assert.Equal(8000, Miss.PcmSampleRate(0b10000000)); // bit 7 is outside the field
    }

    [Fact(DisplayName = "PROTO-11 cameras are devices whose model contains camera")]
    public void CameraModels()
    {
        Assert.True(Mi.IsCamera("chuangmi.camera.077ac1"));
        Assert.True(Mi.IsCamera("isa.camera.hlc7"));
        Assert.False(Mi.IsCamera("zhimi.airpurifier.ma2"));
        Assert.False(Mi.IsCamera("yeelink.light.lamp4"));
    }

    [Fact(DisplayName = "PROTO-24 night-vision modes map to their wire values and back")]
    public void NightVisionModes()
    {
        // Note ON is 0, not 1.
        Assert.Equal(0, (int)NightVisionMode.On);
        Assert.Equal(1, (int)NightVisionMode.Off);
        Assert.Equal(2, (int)NightVisionMode.Auto);

        Assert.Equal(NightVisionMode.On, Mi.NightVisionFromValue(0));
        Assert.Equal(NightVisionMode.Off, Mi.NightVisionFromValue(1));
        Assert.Equal(NightVisionMode.Auto, Mi.NightVisionFromValue(2));
        Assert.Null(Mi.NightVisionFromValue(7)); // an unknown value is null — not a crash
    }
}
