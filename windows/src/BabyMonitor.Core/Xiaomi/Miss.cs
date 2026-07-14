using System.Globalization;
using System.Text;
using BabyMonitor.Core.Json;
using BabyMonitor.Core.Logging;
using BabyMonitor.Core.Net;

namespace BabyMonitor.Core.Xiaomi;

/// <summary>
/// MISS (Mi Secure Streaming) — rides on CS2: command channel 0 for control, media channel 2 for A/V.
/// </summary>
public static class Miss
{
    public const long CmdAuthReq = 0x100;
    public const long CmdVideoStart = 0x102;
    public const long CmdVideoStop = 0x103;
    public const long CmdEncoded = 0x1001;

    public const long CodecH264 = 4;
    public const long CodecH265 = 5;
    public const long CodecPcm = 1024;
    public const long CodecPcmu = 1026;
    public const long CodecPcma = 1027;
    public const long CodecOpus = 1032;

    public const string ModelC200 = "chuangmi.camera.046c04";
    public const string ModelC300 = "chuangmi.camera.72ac1";

    /// <summary>PROTO-20: the (unencrypted) MISS auth payload.</summary>
    public static string AuthPayload(string clientPublicHex, string sign) =>
        new JsonObj()
            .Put("public_key", clientPublicHex)
            .Put("sign", sign)
            .Put("uuid", string.Empty)
            .Put("support_encrypt", 0)
            .ToString();

    /// <summary>PROTO-21: quality mapping + start body.</summary>
    public static string StartMediaBody(string model, string quality, bool audio)
    {
        var q = quality switch
        {
            "sd" => "1",
            "auto" => "0",
            _ => model is ModelC200 or ModelC300 ? "3" : "2",
        };
        var a = audio ? "1" : "0";
        return $"{{\"videoquality\":{q},\"enableaudio\":{a}}}";
    }

    /// <summary>PROTO-21: [BE u32 inner command][body] — the plaintext of an encoded control command.</summary>
    public static byte[] InnerCommand(long cmd, string body = "")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var payload = new byte[4 + bodyBytes.Length];
        payload.PutBeU32(0, cmd);
        Buffer.BlockCopy(bodyBytes, 0, payload, 4, bodyBytes.Length);
        return payload;
    }

    /// <summary>PROTO-22: the 32-byte little-endian media header.</summary>
    public sealed record MediaHeader(long Size, long CodecId, long Sequence, long Flags, long PtsMs);

    public static MediaHeader ParseMediaHeader(byte[] hdr)
    {
        if (hdr.Length < 32)
        {
            throw new ArgumentException("miss: header too small", nameof(hdr));
        }

        return new MediaHeader(
            Size: hdr.LeU32(0),
            CodecId: hdr.LeU32(4),
            Sequence: hdr.LeU32(8),
            Flags: hdr.LeU32(12),
            PtsMs: hdr.LeU64(16));
    }

    /// <summary>PROTO-23: PCM-family sample rate is derived from flags bits 3–6.</summary>
    public static int PcmSampleRate(long flags) => ((flags >> 3) & 0b1111) != 0 ? 16000 : 8000;

    /// <summary>PROTO-23: decode one decrypted media packet into a Frame (null = skip).</summary>
    public static Frame? FrameFromPacket(MediaHeader header, byte[] payload) => header.CodecId switch
    {
        CodecH264 => new Frame.Video("h264", header.PtsMs, header.Sequence, header.Flags, payload),
        CodecH265 => new Frame.Video("h265", header.PtsMs, header.Sequence, header.Flags, payload),
        CodecOpus => new Frame.Audio("opus", 48000, header.PtsMs, header.Sequence, header.Flags, payload),
        CodecPcma or CodecPcmu or CodecPcm => new Frame.Audio(
            header.CodecId switch
            {
                CodecPcma => "pcma",
                CodecPcmu => "pcmu",
                _ => "pcm",
            },
            PcmSampleRate(header.Flags),
            header.PtsMs,
            header.Sequence,
            header.Flags,
            payload),
        _ => null,
    };
}

public sealed class MissClient : IDisposable
{
    private readonly string _model;

    public MissClient(string model, ISocketFactory sockets)
    {
        _model = model;
        Conn = new Cs2Conn(sockets);
    }

    public Cs2Conn Conn { get; }

    /// <summary>The 32-byte NaCl shared key (PROTO-13).</summary>
    public byte[] Key { get; private set; } = Array.Empty<byte>();

    public sealed record ConnectParams(
        string Ip,
        string Vendor,
        byte[] ClientPublic,
        byte[] ClientPrivate,
        string DevicePublicHex,
        string Sign,
        string? Transport = "tcp");

    public async Task ConnectAsync(ConnectParams p, CancellationToken ct = default)
    {
        if (p.Vendor != "cs2")
        {
            throw new XiaomiException($"miss: unsupported vendor {p.Vendor} (only cs2 implemented)");
        }

        Log.I("miss", $"connect ip={p.Ip} model={_model} transport={p.Transport}");
        Key = Crypto.CalcSharedKey(p.DevicePublicHex, p.ClientPrivate.ToHex());
        await Conn.DialAsync(p.Ip, p.Transport, ct).ConfigureAwait(false);
        await LoginAsync(p.ClientPublic.ToHex(), p.Sign, ct).ConfigureAwait(false);
    }

    public async Task StartMediaAsync(string quality = "hd", bool audio = true, CancellationToken ct = default)
    {
        Log.I("miss", $"startMedia quality={quality} audio={audio}");
        await WriteEncodedAsync(
            Miss.InnerCommand(Miss.CmdVideoStart, Miss.StartMediaBody(_model, quality, audio)),
            ct).ConfigureAwait(false);
    }

    public Task StopMediaAsync(CancellationToken ct = default) =>
        WriteEncodedAsync(Miss.InnerCommand(Miss.CmdVideoStop), ct);

    /// <summary>
    /// The next decrypted media frame. Skips unknown codecs and packets that fail to decrypt
    /// (PROTO-23); throws only when the connection is gone.
    /// </summary>
    public async Task<Frame> ReadFrameAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var (hdr, encPayload) = await Conn.ReadPacketAsync(ct).ConfigureAwait(false);
            var header = Miss.ParseMediaHeader(hdr);
            byte[] payload;
            try
            {
                payload = Crypto.ChachaDecode(encPayload, Key);
            }
            catch (ArgumentException)
            {
                continue; // a packet that will not decrypt is skipped, never fatal
            }

            var frame = Miss.FrameFromPacket(header, payload);
            if (frame != null)
            {
                return frame;
            }
        }
    }

    public void Close() => Conn.Close();

    public void Dispose() => Conn.Dispose();

    private async Task LoginAsync(string clientPublicHex, string sign, CancellationToken ct)
    {
        Log.I("miss", "authenticating…");
        await Conn.WriteCommandAsync(
            Miss.CmdAuthReq,
            Encoding.UTF8.GetBytes(Miss.AuthPayload(clientPublicHex, sign)),
            ct).ConfigureAwait(false);

        var (_, data) = await Conn.ReadCommandAsync(ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(data);
        if (!text.Contains("\"result\":\"success\"", StringComparison.Ordinal))
        {
            Log.W("miss", $"auth failed: {Truncate(text, 200)}");
            throw new XiaomiException($"miss: auth failed: {text}");
        }

        Log.I("miss", "auth ok");
    }

    private Task WriteEncodedAsync(byte[] data, CancellationToken ct) =>
        Conn.WriteCommandAsync(Miss.CmdEncoded, Crypto.ChachaEncode(data, Key), ct);

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
