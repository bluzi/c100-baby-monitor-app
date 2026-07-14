using System.Threading.Channels;
using BabyMonitor.Core.Logging;
using BabyMonitor.Core.Net;

namespace BabyMonitor.Core.Xiaomi;

/// <summary>
/// CS2 P2P transport — UDP handshake on :32108, then TCP (the C100 default) or UDP for the data
/// phase. Port of the Kotlin core's Cs2.kt, itself of c100's cs2.ts, itself of go2rtc.
/// </summary>
public static class Cs2
{
    public const int Magic = 0xf1;
    public const int MagicDrw = 0xd1;
    public const int MagicTcp = 0x68;
    public const int MsgLanSearch = 0x30;
    public const int MsgPunchPkt = 0x41;
    public const int MsgP2PRdyUdp = 0x42;
    public const int MsgP2PRdyTcp = 0x43;
    public const int MsgDrw = 0xd0;
    public const int MsgDrwAck = 0xd1;
    public const int MsgPing = 0xe0;
    public const int MsgPong = 0xe1;
    public const int HandshakePort = 32108;

    public static byte[] LanSearch => new byte[] { Magic, MsgLanSearch, 0, 0 };

    public static byte[] Ping => new byte[] { Magic, MsgPing, 0, 0 };

    /// <summary>PROTO-17: DRW frame carrying a command record (BE u32 size + LE u32 cmd + payload).</summary>
    public static byte[] MarshalCmd(int channel, int seq, long cmd, byte[] payload)
    {
        var req = new byte[16 + payload.Length];
        req[0] = Magic;
        req[1] = MsgDrw;
        req.PutBeU16(2, 12 + payload.Length);
        req[4] = MagicDrw;
        req[5] = (byte)channel;
        req.PutBeU16(6, seq);
        req.PutBeU32(8, 4 + payload.Length);
        req.PutBeU32(12, cmd);
        Buffer.BlockCopy(payload, 0, req, 16, payload.Length);
        return req;
    }

    /// <summary>PROTO-16: TCP framing — 8-byte header, BE u16 size at 0, magic 0x68 at 2.</summary>
    public static byte[] TcpFrame(byte[] body)
    {
        var buf = new byte[8 + body.Length];
        buf.PutBeU16(0, body.Length);
        buf[2] = MagicTcp;
        Buffer.BlockCopy(body, 0, buf, 8, body.Length);
        return buf;
    }

    public static byte[] UdpAck(int channel, byte seqHi, byte seqLo) => new byte[]
    {
        Magic, MsgDrwAck, 0, 6, MagicDrw, (byte)channel, 0, 1, seqHi, seqLo,
    };
}

/// <summary>
/// PROTO-18: reassembles 4-byte BE length-prefixed records out of the DRW payload byte stream,
/// regardless of how the bytes were split across DRW packets.
/// </summary>
public sealed class RecordAssembler
{
    /// <summary>
    /// PROTO-18: no legitimate record comes close (commands are small, a keyframe is tens of KB). A
    /// prefix past this is corrupt or hostile input — the connection is dead. Without the check, a
    /// length near 2^31 would try to allocate gigabytes and take the process with it.
    /// </summary>
    public const int MaxRecordBytes = 8 * 1024 * 1024;

    private byte[] _pending = Array.Empty<byte>();
    private int _waitSize;

    public IReadOnlyList<byte[]> Push(byte[] chunk)
    {
        _pending = Bytes.Concat(_pending, chunk);
        var out_ = new List<byte[]>();
        while (true)
        {
            if (_waitSize == 0)
            {
                if (_pending.Length < 4)
                {
                    break;
                }

                var size = _pending.BeU32(0);
                if (size > MaxRecordBytes)
                {
                    throw new XiaomiException($"cs2: corrupt record length {size} — dropping the connection");
                }

                _waitSize = (int)size;
                _pending = _pending.Slice(4, _pending.Length);
                continue; // a zero-length record is consumed by its prefix alone
            }

            // A record completed by even a 1-byte chunk must come out now — a small command record
            // must never sit here waiting for unrelated bytes to flush it.
            if (_pending.Length < _waitSize)
            {
                break;
            }

            out_.Add(_pending.Slice(0, _waitSize));
            _pending = _pending.Slice(_waitSize, _pending.Length);
            _waitSize = 0;
        }

        return out_;
    }
}

internal interface IRawConn
{
    bool IsTcp { get; }

    Task WriteAsync(byte[] data, CancellationToken ct);

    Task<byte[]> ReadAsync(CancellationToken ct);

    void Close();
}

public sealed class Cs2Conn : IDisposable
{
    private readonly ISocketFactory _sockets;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    // PROTO-18: BOTH channels carry length-prefixed records that span DRW frames.
    private readonly RecordAssembler _ch0Assembler = new();
    private readonly RecordAssembler _ch2Assembler = new();
    private readonly Channel<byte[]> _channel0 = Channel.CreateUnbounded<byte[]>(); // command records
    private readonly Channel<byte[]> _channel2 = Channel.CreateUnbounded<byte[]>(); // media records

    private IRawConn? _raw;
    private int _seqCh0;
    private volatile bool _closed;

    public Cs2Conn(ISocketFactory sockets) => _sockets = sockets;

    public bool IsTcp => _raw?.IsTcp == true;

    /// <summary>PROTO-15: LAN search → punch echo → P2P ready, then TCP or UDP data phase.</summary>
    public async Task DialAsync(string host, string? transport = "tcp", CancellationToken ct = default)
    {
        Log.I("cs2", $"dial {host}:{Cs2.HandshakePort} transport={transport}");
        var udp = _sockets.Udp();
        await udp.BindAsync().ConfigureAwait(false);
        var remotePort = Cs2.HandshakePort;

        async Task<byte[]> WriteUntilAsync(byte[] req, Func<byte[], bool> accept)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(5000);

            // The first send is synchronous so it always targets the current port; the
            // retransmitter only handles lost packets after that.
            try
            {
                await udp.SendAsync(req, host, remotePort, attempt.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A send that fails now is retried below.
            }

            var sender = Task.Run(
                async () =>
                {
                    while (!attempt.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, attempt.Token).ConfigureAwait(false);
                        try
                        {
                            await udp.SendAsync(req, host, remotePort, attempt.Token).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            // Keep trying until the overall attempt times out.
                        }
                    }
                },
                attempt.Token);

            try
            {
                while (true)
                {
                    var dg = await udp.ReceiveAsync(attempt.Token).ConfigureAwait(false);
                    if (dg.Host != host || dg.Data.Length < 4)
                    {
                        continue;
                    }

                    if (accept(dg.Data))
                    {
                        remotePort = dg.Port;
                        return dg.Data;
                    }
                }
            }
            finally
            {
                await attempt.CancelAsync().ConfigureAwait(false);
                await sender.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            }
        }

        byte[] punch;
        try
        {
            punch = await WriteUntilAsync(Cs2.LanSearch, data => data[1] == Cs2.MsgPunchPkt)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            udp.Close();
            if (ct.IsCancellationRequested)
            {
                throw; // the user stopped monitoring; that is not a camera failure
            }

            Log.W("cs2", $"handshake timed out at LAN search — camera {host} unreachable on :{Cs2.HandshakePort}? (same network?)");
            throw new XiaomiException("cs2: handshake timeout (LAN search)", e);
        }

        Log.I("cs2", $"got punch packet from {host}:{remotePort}");

        var wantUdp = transport is null or "udp";
        var wantTcp = transport is null or "tcp";
        byte[] ready;
        try
        {
            ready = await WriteUntilAsync(
                punch,
                data =>
                {
                    var msg = data[1];
                    return (wantUdp && msg == Cs2.MsgP2PRdyUdp) || (wantTcp && msg == Cs2.MsgP2PRdyTcp);
                }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            udp.Close();
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            Log.W("cs2", $"handshake timed out waiting for P2P-ready from {host}");
            throw new XiaomiException("cs2: handshake timeout (P2P ready)", e);
        }

        if (ready[1] == Cs2.MsgP2PRdyTcp)
        {
            Log.I("cs2", $"P2P ready (TCP) — connecting tcp {host}:{remotePort}");
            udp.Close();
            _raw = await OpenTcpAsync(host, remotePort, ct).ConfigureAwait(false);
        }
        else
        {
            Log.I("cs2", $"P2P ready (UDP) on {host}:{remotePort}");
            _raw = new UdpConn(udp, host, remotePort);
        }

        Log.I("cs2", $"transport up: {(_raw.IsTcp ? "cs2+tcp" : "cs2+udp")}");

        _ = Task.Run(WorkerLoopAsync, CancellationToken.None);

        // PROTO-19: keepalive on an independent timer; never answer PING with PONG.
        if (_raw.IsTcp)
        {
            _ = Task.Run(
                async () =>
                {
                    while (!_closed)
                    {
                        try
                        {
                            await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                            if (_closed)
                            {
                                break;
                            }

                            await WriteRawAsync(Cs2.Ping).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception)
                        {
                            // A failed keepalive is the reader's problem to notice, not ours.
                        }
                    }
                },
                CancellationToken.None);
        }
    }

    public async Task WriteCommandAsync(long cmd, byte[] data, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = Cs2.MarshalCmd(0, _seqCh0, cmd, data);
            _seqCh0 = (_seqCh0 + 1) & 0xffff;
            var raw = _raw ?? throw new XiaomiException("cs2: not connected");
            await raw.WriteAsync(req, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>PROTO-18: one command record — LE u32 command id + payload.</summary>
    public async Task<(long Cmd, byte[] Payload)> ReadCommandAsync(CancellationToken ct = default)
    {
        var buf = await ReadRecordAsync(_channel0, ct).ConfigureAwait(false);
        if (buf.Length < 4)
        {
            throw new XiaomiException("cs2: short command record");
        }

        return (buf.LeU32(0), buf.Slice(4, buf.Length));
    }

    /// <summary>PROTO-22: one media packet — 32-byte header + encrypted payload.</summary>
    public async Task<(byte[] Header, byte[] Payload)> ReadPacketAsync(CancellationToken ct = default)
    {
        var buf = await ReadRecordAsync(_channel2, ct).ConfigureAwait(false);
        if (buf.Length < 32)
        {
            throw new XiaomiException("miss: packet header too small");
        }

        return (buf.Slice(0, 32), buf.Slice(32, buf.Length));
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _channel0.Writer.TryComplete();
        _channel2.Writer.TryComplete();
        try
        {
            _raw?.Close();
        }
        catch (Exception)
        {
            // Closing what is already gone is not a failure.
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    public void Dispose()
    {
        Close();
        _cts.Dispose();
        _writeLock.Dispose();
    }

    private static async Task<byte[]> ReadRecordAsync(Channel<byte[]> channel, CancellationToken ct)
    {
        try
        {
            return await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException e)
        {
            throw e.InnerException as XiaomiException
                ?? new XiaomiException("cs2: connection closed", e);
        }
    }

    private async Task<IRawConn> OpenTcpAsync(string host, int port, CancellationToken ct)
    {
        var tcp = _sockets.Tcp();
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new TcpConn(tcp);
    }

    private async Task WriteRawAsync(byte[] frame)
    {
        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (_raw is { } raw)
            {
                await raw.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WorkerLoopAsync()
    {
        var conn = _raw;
        if (conn == null)
        {
            return;
        }

        while (!_closed)
        {
            // Anything thrown here — a failed read, or a corrupt record from the assembler
            // (PROTO-18) — is connection-fatal, never process-fatal.
            try
            {
                var buf = await conn.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (buf.Length < 4)
                {
                    continue;
                }

                if (buf[1] != Cs2.MsgDrw)
                {
                    continue; // PROTO-19: PING is deliberately not answered
                }

                if (buf.Length < 8)
                {
                    continue;
                }

                var ch = buf[5];
                var payload = buf.Slice(8, buf.Length);
                if (!conn.IsTcp)
                {
                    try
                    {
                        await conn.WriteAsync(Cs2.UdpAck(ch, buf[6], buf[7]), _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A lost ack costs a retransmit, not the connection.
                    }
                }

                switch (ch)
                {
                    case 0:
                        foreach (var record in _ch0Assembler.Push(payload))
                        {
                            _channel0.Writer.TryWrite(record);
                        }

                        break;
                    case 2:
                        foreach (var record in _ch2Assembler.Push(payload))
                        {
                            _channel2.Writer.TryWrite(record);
                        }

                        break;
                }
            }
            catch (Exception e)
            {
                if (!_closed)
                {
                    Log.W("cs2", $"connection lost: {e.Message}");
                    var lost = new XiaomiException("cs2: connection lost", e);
                    _channel0.Writer.TryComplete(lost);
                    _channel2.Writer.TryComplete(lost);
                }

                return;
            }
        }
    }

    private sealed class UdpConn : IRawConn
    {
        private readonly IUdpSocket _udp;
        private readonly string _host;
        private readonly int _port;

        public UdpConn(IUdpSocket udp, string host, int port)
        {
            _udp = udp;
            _host = host;
            _port = port;
        }

        public bool IsTcp => false;

        public Task WriteAsync(byte[] data, CancellationToken ct) => _udp.SendAsync(data, _host, _port, ct);

        public async Task<byte[]> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                var dg = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (dg.Host == _host)
                {
                    return dg.Data;
                }
            }
        }

        public void Close() => _udp.Close();
    }

    private sealed class TcpConn : IRawConn
    {
        private readonly ITcpSocket _tcp;

        public TcpConn(ITcpSocket tcp) => _tcp = tcp;

        public bool IsTcp => true;

        public Task WriteAsync(byte[] data, CancellationToken ct) => _tcp.WriteAsync(Cs2.TcpFrame(data), ct);

        public async Task<byte[]> ReadAsync(CancellationToken ct)
        {
            var header = await _tcp.ReadExactAsync(8, ct).ConfigureAwait(false);
            return await _tcp.ReadExactAsync(header.BeU16(0), ct).ConfigureAwait(false);
        }

        public void Close() => _tcp.Close();
    }
}
