namespace BabyMonitor.Core.Monitor;

/// <summary>
/// H.265 bitstream shape, shared by every platform's decoder.
///
/// Stream shape (learned from the working c100 implementation): the camera sends VPS/SPS/PPS in their
/// own "config-only" access units, separate from keyframes — so parameter sets must be cached across
/// frames and the decoder configured at the next keyframe. MediaCodec (Android), VideoToolbox (Apple)
/// and Media Foundation (Windows) all need exactly this, so it lives here rather than being
/// re-derived per platform.
/// </summary>
public static class Hevc
{
    public const int NalVps = 32;
    public const int NalSps = 33;
    public const int NalPps = 34;

    /// <summary>Payload range [Start, End) of one NAL unit, and its type.</summary>
    public readonly record struct Nal(int Type, int Start, int End);

    /// <summary>Split an Annex-B buffer (3- or 4-byte start codes) into NAL payload ranges.</summary>
    public static IReadOnlyList<Nal> ParseNals(byte[] data)
    {
        var nals = new List<Nal>();
        var payloadStart = -1;

        void CloseAt(int end)
        {
            if (payloadStart >= 0 && payloadStart < end)
            {
                nals.Add(new Nal((data[payloadStart] >> 1) & 0x3f, payloadStart, end));
            }
        }

        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                var three = data[i + 2] == 1;
                var four = !three && data[i + 2] == 0 && i + 3 < data.Length && data[i + 3] == 1;
                if (three || four)
                {
                    CloseAt(i);
                    payloadStart = i + (three ? 3 : 4);
                    i = payloadStart;
                    continue;
                }
            }

            i++;
        }

        CloseAt(data.Length);
        return nals;
    }

    /// <summary>VCL NAL types are 0..31; IRAP (BLA/IDR/CRA, 16..23) can start decoding.</summary>
    public static bool IsVcl(int type) => type < 32;

    public static bool IsIrap(int type) => type is >= 16 and <= 23;

    public static bool HasVclNal(byte[] data) => ParseNals(data).Any(n => IsVcl(n.Type));

    public static bool IsKeyframe(byte[] data) => ParseNals(data).Any(n => IsIrap(n.Type));
}

/// <summary>
/// Caches the stream's parameter sets as they fly by (they arrive in separate access units) and yields
/// them once all three have been seen. Pure — unit-tested.
/// </summary>
public sealed class HevcParamCache
{
    private readonly Dictionary<int, byte[]> _sets = new();

    public bool Complete =>
        _sets.ContainsKey(Hevc.NalVps) && _sets.ContainsKey(Hevc.NalSps) && _sets.ContainsKey(Hevc.NalPps);

    public void Observe(byte[] data)
    {
        foreach (var nal in Hevc.ParseNals(data))
        {
            if (nal.Type is Hevc.NalVps or Hevc.NalSps or Hevc.NalPps)
            {
                _sets[nal.Type] = data[nal.Start..nal.End];
            }
        }
    }

    /// <summary>The raw VPS, SPS and PPS payloads (no start codes), or null until all three are known.</summary>
    public IReadOnlyList<byte[]>? ParameterSets() => Complete
        ? new[] { _sets[Hevc.NalVps], _sets[Hevc.NalSps], _sets[Hevc.NalPps] }
        : null;

    /// <summary>00 00 00 01 VPS ‖ 00 00 00 01 SPS ‖ 00 00 00 01 PPS, or null until complete.</summary>
    public byte[]? Csd()
    {
        var sets = ParameterSets();
        if (sets == null)
        {
            return null;
        }

        var startCode = new byte[] { 0, 0, 0, 1 };
        var out_ = Array.Empty<byte>();
        foreach (var set in sets)
        {
            out_ = Xiaomi.Bytes.Concat(out_, startCode, set);
        }

        return out_;
    }
}

/// <summary>
/// The picture's size, read out of the SPS.
///
/// Neither the phone nor the Mac needed this: MediaCodec and VideoToolbox both take the parameter sets
/// and work the size out themselves. **Media Foundation does not** — a Windows media type must carry a
/// frame size before it will decode a single byte, so on this platform the size is not a nicety, it is
/// the difference between a picture and a black rectangle.
///
/// It also gives DESK-12 its number: the window takes the camera's shape, and the camera's shape is
/// this.
/// </summary>
public static class HevcSps
{
    /// <summary>
    /// Width and height in luma samples, with the conformance window applied, or null when the SPS
    /// cannot be read. A null is never fatal: the caller falls back to a sane default rather than
    /// dropping the video (LIVE-7).
    /// </summary>
    public static (int Width, int Height)? Dimensions(byte[] spsNal)
    {
        try
        {
            if (spsNal.Length < 4)
            {
                return null;
            }

            var rbsp = StripEmulation(spsNal, 2); // skip the 2-byte NAL header
            var r = new BitReader(rbsp);

            r.Skip(4); // sps_video_parameter_set_id
            var maxSubLayersMinus1 = (int)r.Read(3);
            r.Skip(1); // sps_temporal_id_nesting_flag
            SkipProfileTierLevel(r, maxSubLayersMinus1);

            r.ReadUe(); // sps_seq_parameter_set_id
            var chromaFormatIdc = (int)r.ReadUe();
            if (chromaFormatIdc == 3)
            {
                r.Skip(1); // separate_colour_plane_flag
            }

            var width = (int)r.ReadUe(); // pic_width_in_luma_samples
            var height = (int)r.ReadUe(); // pic_height_in_luma_samples

            if (r.Read(1) == 1)
            {
                // conformance_window_flag: the picture is cropped, and the crop is in chroma units.
                var left = (int)r.ReadUe();
                var right = (int)r.ReadUe();
                var top = (int)r.ReadUe();
                var bottom = (int)r.ReadUe();
                var subWidth = chromaFormatIdc is 1 or 2 ? 2 : 1;
                var subHeight = chromaFormatIdc == 1 ? 2 : 1;
                width -= subWidth * (left + right);
                height -= subHeight * (top + bottom);
            }

            // A camera that sends something absurd is a camera we do not believe.
            if (width is < 16 or > 8192 || height is < 16 or > 8192)
            {
                return null;
            }

            return (width, height);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void SkipProfileTierLevel(BitReader r, int maxSubLayersMinus1)
    {
        // general_profile_space(2) tier(1) profile_idc(5) compatibility(32) flags(48) level(8)
        r.Skip(2 + 1 + 5 + 32 + 48 + 8);

        var profilePresent = new bool[maxSubLayersMinus1];
        var levelPresent = new bool[maxSubLayersMinus1];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = r.Read(1) == 1;
            levelPresent[i] = r.Read(1) == 1;
        }

        if (maxSubLayersMinus1 > 0)
        {
            for (var i = maxSubLayersMinus1; i < 8; i++)
            {
                r.Skip(2); // reserved_zero_2bits
            }
        }

        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i])
            {
                r.Skip(2 + 1 + 5 + 32 + 48);
            }

            if (levelPresent[i])
            {
                r.Skip(8);
            }
        }
    }

    /// <summary>Remove the 0x03 bytes the encoder inserted to keep start codes out of the payload.</summary>
    private static byte[] StripEmulation(byte[] data, int from)
    {
        var out_ = new List<byte>(data.Length - from);
        var zeros = 0;
        for (var i = from; i < data.Length; i++)
        {
            var b = data[i];
            if (zeros >= 2 && b == 0x03)
            {
                zeros = 0;
                continue; // the emulation-prevention byte itself is not payload
            }

            zeros = b == 0 ? zeros + 1 : 0;
            out_.Add(b);
        }

        return out_.ToArray();
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bit;

        public BitReader(byte[] data) => _data = data;

        public uint Read(int bits)
        {
            uint value = 0;
            for (var i = 0; i < bits; i++)
            {
                var byteIndex = _bit >> 3;
                if (byteIndex >= _data.Length)
                {
                    throw new InvalidOperationException("sps: ran off the end of the bitstream");
                }

                var bit = (_data[byteIndex] >> (7 - (_bit & 7))) & 1;
                value = (value << 1) | (uint)bit;
                _bit++;
            }

            return value;
        }

        public void Skip(int bits)
        {
            _bit += bits;
            if (_bit > _data.Length * 8)
            {
                throw new InvalidOperationException("sps: ran off the end of the bitstream");
            }
        }

        /// <summary>Unsigned Exp-Golomb, the code every dimension in an SPS hides behind.</summary>
        public uint ReadUe()
        {
            var leadingZeros = 0;
            while (Read(1) == 0)
            {
                leadingZeros++;
                if (leadingZeros > 31)
                {
                    throw new InvalidOperationException("sps: malformed Exp-Golomb code");
                }
            }

            if (leadingZeros == 0)
            {
                return 0;
            }

            return (uint)((1 << leadingZeros) - 1 + Read(leadingZeros));
        }
    }
}
