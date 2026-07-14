using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

// Support for LIVE-1/LIVE-7: the camera interleaves config-only access units (VPS/SPS/PPS) with
// keyframes, so parameter sets must be cached across frames.

public class HevcTest
{
    private static byte[] Nal(int type, params int[] payload) =>
        new byte[] { 0, 0, 0, 1, (byte)(type << 1), 1 }
            .Concat(payload.Select(p => (byte)p))
            .ToArray();

    private static byte[] Nal3(int type) => new byte[] { 0, 0, 1, (byte)(type << 1), 1 };

    [Fact(DisplayName = "parseNals splits 3- and 4-byte start codes and reads types")]
    public void ParseNals()
    {
        var data = Nal(32).Concat(Nal3(33)).Concat(Nal(34)).Concat(Nal(19, 9, 9)).ToArray();
        var types = Hevc.ParseNals(data).Select(n => n.Type).ToList();
        Assert.Equal(new[] { 32, 33, 34, 19 }, types);
    }

    [Fact(DisplayName = "keyframe and VCL detection follow the IRAP ranges")]
    public void KeyframeDetection()
    {
        Assert.True(Hevc.IsKeyframe(Nal(19))); // IDR_W_RADL
        Assert.True(Hevc.IsKeyframe(Nal(21))); // CRA
        Assert.False(Hevc.IsKeyframe(Nal(1))); // trailing picture
        Assert.True(Hevc.HasVclNal(Nal(1)));
        Assert.False(Hevc.HasVclNal(Nal(32).Concat(Nal(33)).ToArray())); // config-only access unit
    }

    [Fact(DisplayName = "parameter sets cached across separate access units yield a csd")]
    public void ParamCacheAcrossAccessUnits()
    {
        var cache = new HevcParamCache();
        cache.Observe(Nal(32, 7)); // VPS alone
        Assert.Null(cache.Csd());
        cache.Observe(Nal(33, 8).Concat(Nal(34, 9)).ToArray()); // SPS+PPS in a later AU

        var csd = cache.Csd();
        Assert.NotNull(csd);
        // The csd is the three cached NALs, 4-byte start codes, in VPS/SPS/PPS order.
        Assert.Equal(Nal(32, 7).Concat(Nal(33, 8)).Concat(Nal(34, 9)).ToArray(), csd);
    }

    [Fact(DisplayName = "newer parameter sets replace older ones")]
    public void NewerParamsWin()
    {
        var cache = new HevcParamCache();
        cache.Observe(Nal(32, 1).Concat(Nal(33, 1)).Concat(Nal(34, 1)).ToArray());
        cache.Observe(Nal(33, 2)); // the stream re-announces its SPS
        Assert.Equal(
            Nal(32, 1).Concat(Nal(33, 2)).Concat(Nal(34, 1)).ToArray(),
            cache.Csd());
    }
}

/// <summary>
/// WIN-19/WIN-20. Media Foundation will not decode a byte without a frame size, so on Windows the
/// picture's dimensions have to be read out of the SPS — something neither the phone nor the Mac ever
/// needed. This is the test that says the bit-walk through profile_tier_level and the Exp-Golomb
/// fields actually lands on the right numbers.
/// </summary>
public class HevcSpsTest
{
    [Fact(DisplayName = "WIN-19 the picture size is read out of the SPS")]
    public void ReadsDimensions()
    {
        var sps = BuildSps(1920, 1080);
        Assert.Equal((1920, 1080), HevcSps.Dimensions(sps));
    }

    [Fact(DisplayName = "WIN-19 the conformance window is applied — 1088 coded is 1080 shown")]
    public void AppliesTheConformanceWindow()
    {
        // A 1080p encoder codes 1088 rows (a multiple of the CTU size) and crops 4 chroma rows off the
        // bottom. Ignoring the crop would stretch every frame by 8 pixels.
        var sps = BuildSps(1920, 1088, cropBottom: 4);
        Assert.Equal((1920, 1080), HevcSps.Dimensions(sps));
    }

    [Fact(DisplayName = "WIN-19 emulation-prevention bytes are stripped before the bits are read")]
    public void StripsEmulationPrevention()
    {
        // 2304x1296 (the C100's other mode) produces a zero run in the bitstream, which the encoder
        // breaks up with a 0x03. Reading it as payload would shift every field that follows.
        var sps = BuildSps(2304, 1296);
        Assert.Contains(new byte[] { 0, 0, 3 }, Windows(sps, 3));
        Assert.Equal((2304, 1296), HevcSps.Dimensions(sps));
    }

    [Fact(DisplayName = "WIN-20 an SPS that cannot be read is a null, never a crash")]
    public void GarbageIsNull()
    {
        Assert.Null(HevcSps.Dimensions(new byte[] { 0x42, 0x01 }));
        Assert.Null(HevcSps.Dimensions(new byte[] { 0x42 }));
        Assert.Null(HevcSps.Dimensions(new byte[] { 0x42, 0x01, 0xff, 0xff, 0xff, 0xff }));
    }

    private static IEnumerable<byte[]> Windows(byte[] data, int size)
    {
        for (var i = 0; i + size <= data.Length; i++)
        {
            yield return data[i..(i + size)];
        }
    }

    /// <summary>
    /// A minimal but real H.265 SPS NAL: 2-byte header, profile_tier_level, then the Exp-Golomb fields
    /// the parser walks — emulation-prevented exactly as an encoder would emit it.
    /// </summary>
    private static byte[] BuildSps(int width, int height, int cropBottom = 0)
    {
        var w = new BitWriter();
        w.Write(0, 4); // sps_video_parameter_set_id
        w.Write(0, 3); // sps_max_sub_layers_minus1
        w.Write(1, 1); // sps_temporal_id_nesting_flag

        // profile_tier_level(1, 0): 2 + 1 + 5 + 32 + 48 + 8 bits.
        w.Write(0, 2); // general_profile_space
        w.Write(0, 1); // general_tier_flag
        w.Write(1, 5); // general_profile_idc (Main)
        w.Write(0b0110_0000_0000_0000_0000_0000_0000_0000, 32); // compatibility flags
        w.Write(0, 24); // constraint flags, first half
        w.Write(0, 24); // constraint flags, second half
        w.Write(93, 8); // general_level_idc (Level 3.1)

        w.WriteUe(0); // sps_seq_parameter_set_id
        w.WriteUe(1); // chroma_format_idc = 4:2:0
        w.WriteUe((uint)width); // pic_width_in_luma_samples
        w.WriteUe((uint)height); // pic_height_in_luma_samples

        if (cropBottom > 0)
        {
            w.Write(1, 1); // conformance_window_flag
            w.WriteUe(0); // left
            w.WriteUe(0); // right
            w.WriteUe(0); // top
            w.WriteUe((uint)cropBottom); // bottom, in chroma units
        }
        else
        {
            w.Write(0, 1); // conformance_window_flag
        }

        w.Write(1, 1); // rbsp_stop_one_bit — nothing past here is read
        var rbsp = w.ToArray();

        var nal = new List<byte> { (byte)(Hevc.NalSps << 1), 0x01 };
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                nal.Add(0x03); // the emulation-prevention byte an encoder inserts
                zeros = 0;
            }

            nal.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return nal.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _current;
        private int _bits;

        public void Write(uint value, int bits)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                var bit = (value >> i) & 1;
                _current = (_current << 1) | (int)bit;
                _bits++;
                if (_bits != 8)
                {
                    continue;
                }

                _bytes.Add((byte)_current);
                _current = 0;
                _bits = 0;
            }
        }

        /// <summary>Unsigned Exp-Golomb, the code every dimension in an SPS hides behind.</summary>
        public void WriteUe(uint value)
        {
            var v = value + 1;
            var length = 0;
            while (v >> length != 0)
            {
                length++;
            }

            Write(0, length - 1);
            Write(v, length);
        }

        public byte[] ToArray()
        {
            var out_ = new List<byte>(_bytes);
            if (_bits > 0)
            {
                out_.Add((byte)(_current << (8 - _bits)));
            }

            return out_.ToArray();
        }
    }
}
