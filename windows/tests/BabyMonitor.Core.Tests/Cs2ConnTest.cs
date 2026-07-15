using System.Text;
using System.Threading.Channels;
using BabyMonitor.Core.Net;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

internal sealed class FakeUdp : IUdpSocket
{
    public Channel<Datagram> Incoming { get; } = Channel.CreateUnbounded<Datagram>();

    public List<(byte[] Data, string Host, int Port)> Sent { get; } = new();

    public bool Closed { get; private set; }

    /// <summary>One transient read failure to raise before the next datagram, for PROTO-25.</summary>
    public SocketClosedException? ThrowOnceOnReceive { get; set; }

    public Task BindAsync() => Task.CompletedTask;

    public Task SendAsync(byte[] data, string host, int port, CancellationToken ct = default)
    {
        lock (Sent)
        {
            Sent.Add((data, host, port));
        }

        return Task.CompletedTask;
    }

    public async Task<Datagram> ReceiveAsync(CancellationToken ct = default)
    {
        var toThrow = ThrowOnceOnReceive;
        if (toThrow != null)
        {
            ThrowOnceOnReceive = null;
            throw toThrow;
        }

        return await Incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    public void Close() => Closed = true;

    public void Dispose() => Close();

    public IReadOnlyList<(byte[] Data, string Host, int Port)> SentSnapshot()
    {
        lock (Sent)
        {
            return Sent.ToList();
        }
    }
}

internal sealed class FakeTcp : ITcpSocket
{
    private readonly Channel<byte> _incoming = Channel.CreateUnbounded<byte>();

    public List<byte[]> Written { get; } = new();

    public (string Host, int Port)? ConnectedTo { get; private set; }

    public void Feed(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            _incoming.Writer.TryWrite(b);
        }
    }

    public Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        ConnectedTo = (host, port);
        return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        lock (Written)
        {
            Written.Add(data);
        }

        return Task.CompletedTask;
    }

    public async Task<byte[]> ReadExactAsync(int n, CancellationToken ct = default)
    {
        var out_ = new byte[n];
        for (var i = 0; i < n; i++)
        {
            out_[i] = await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
        }

        return out_;
    }

    public void Close()
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeSockets : ISocketFactory
{
    public FakeUdp Udp_ { get; } = new();

    public FakeTcp Tcp_ { get; } = new();

    public IUdpSocket Udp() => Udp_;

    public ITcpSocket Tcp() => Tcp_;
}

public class Cs2ConnTest
{
    private const string Camera = "192.168.1.50";

    private static byte[] PunchPacket() => new byte[] { 0xf1, 0x41, 0, 4, 9, 9, 9, 9 };

    private static byte[] ReadyTcp() => new byte[] { 0xf1, 0x43, 0, 0 };

    private static byte[] ReadyUdp() => new byte[] { 0xf1, 0x42, 0, 0 };

    /// <summary>DRW frame: [0xF1 0xD0 size(BE16) 0xD1 ch seq(BE16)] + chunk.</summary>
    private static byte[] DrwFrame(byte[] chunk, int channel, int seq)
    {
        var b = new byte[8 + chunk.Length];
        b[0] = 0xf1;
        b[1] = 0xd0;
        b.PutBeU16(2, 4 + 4 + chunk.Length);
        b[4] = 0xd1;
        b[5] = (byte)channel;
        b.PutBeU16(6, seq);
        Buffer.BlockCopy(chunk, 0, b, 8, chunk.Length);
        return b;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms = 5000)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms)).ConfigureAwait(false);
        Assert.Same(task, done); // a hang here is a real failure, not a slow machine
        return await task.ConfigureAwait(false);
    }

    [Fact(DisplayName = "PROTO-15 handshake sends LAN search, echoes the punch, then dials TCP on the announced port")]
    public async Task Handshake()
    {
        var sockets = new FakeSockets();
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(PunchPacket(), Camera, 55555));
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(ReadyTcp(), Camera, 55555));

        using var conn = new Cs2Conn(sockets);
        await conn.DialAsync(Camera, "tcp");

        // First thing on the wire: LAN search to the handshake port.
        var sent = sockets.Udp_.SentSnapshot();
        Assert.Equal(Cs2.LanSearch, sent[0].Data);
        Assert.Equal(Camera, sent[0].Host);
        Assert.Equal(Cs2.HandshakePort, sent[0].Port);

        // The punch packet is echoed back verbatim to the port it came from.
        Assert.Contains(sent, s => s.Data.SequenceEqual(PunchPacket()) && s.Port == 55555);

        // TCP data phase on the announced port; the handshake socket is released.
        Assert.Equal((Camera, 55555), sockets.Tcp_.ConnectedTo);
        Assert.True(sockets.Udp_.Closed);
        Assert.True(conn.IsTcp);
        conn.Close();
    }

    [Fact(DisplayName = "PROTO-25 the handshake survives a transient udp read failure and connects")]
    public async Task HandshakeSurvivesTransientReadFailure()
    {
        var sockets = new FakeSockets();

        // Windows raises the camera's ICMP port-unreachable as a connection-reset on the next read;
        // it must not abort the handshake — the retransmit is still firing.
        sockets.Udp_.ThrowOnceOnReceive = new SocketClosedException("udp: connection reset by peer");
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(PunchPacket(), Camera, 55555));
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(ReadyTcp(), Camera, 55555));

        using var conn = new Cs2Conn(sockets);
        await conn.DialAsync(Camera, "tcp");

        Assert.Equal((Camera, 55555), sockets.Tcp_.ConnectedTo);
        Assert.True(conn.IsTcp);
        conn.Close();
    }

    [Fact(DisplayName = "PROTO-15 datagrams from other hosts are ignored during the handshake")]
    public async Task ImpostorsAreIgnored()
    {
        var sockets = new FakeSockets();
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(PunchPacket(), "10.0.0.99", 55555)); // impostor
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(PunchPacket(), Camera, 55555));
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(ReadyTcp(), Camera, 55555));

        using var conn = new Cs2Conn(sockets);
        await conn.DialAsync(Camera, "tcp");
        Assert.Equal((Camera, 55555), sockets.Tcp_.ConnectedTo);
        conn.Close();
    }

    [Fact(DisplayName = "PROTO-16+18 tcp DRW payloads reassemble into command records across frame boundaries")]
    public async Task CommandRecordsReassemble()
    {
        var sockets = new FakeSockets();
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(PunchPacket(), Camera, 32108));
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(ReadyTcp(), Camera, 32108));

        using var conn = new Cs2Conn(sockets);
        await conn.DialAsync(Camera, "tcp");

        // One command record: [BE u32 len][LE u32 cmd][payload], split across two DRW frames.
        var payload = Encoding.UTF8.GetBytes("auth-ok");
        var record = new byte[8 + payload.Length];
        record.PutBeU32(0, 4 + payload.Length);
        record.PutLeU32(4, 0x101);
        Buffer.BlockCopy(payload, 0, record, 8, payload.Length);

        const int split = 5;
        sockets.Tcp_.Feed(Cs2.TcpFrame(DrwFrame(record.Slice(0, split), 0, 0)));
        sockets.Tcp_.Feed(Cs2.TcpFrame(DrwFrame(record.Slice(split, record.Length), 0, 1)));

        var (cmd, data) = await WithTimeout(conn.ReadCommandAsync());
        Assert.Equal(0x101L, cmd);
        Assert.Equal(payload, data);
        conn.Close();
    }

    [Fact(DisplayName = "PROTO-17 the udp data phase acks DRW packets and routes channels")]
    public async Task UdpDataPhaseAcks()
    {
        var sockets = new FakeSockets();
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(PunchPacket(), Camera, 32108));
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(ReadyUdp(), Camera, 32108));

        using var conn = new Cs2Conn(sockets);
        await conn.DialAsync(Camera, "udp");
        Assert.False(conn.IsTcp);

        // One length-prefixed media record (PROTO-18): 32-byte header + 8 payload bytes.
        var record = new byte[4 + 40];
        record.PutBeU32(0, 40);
        var frame = DrwFrame(record, channel: 2, seq: 0x0304);
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(frame, Camera, 32108));

        var (hdr, body) = await WithTimeout(conn.ReadPacketAsync());
        Assert.Equal(32, hdr.Length);
        Assert.Equal(8, body.Length);

        // The DRW was acked with its channel + sequence.
        var expectedAck = Cs2.UdpAck(2, 0x03, 0x04);
        Assert.Contains(sockets.Udp_.SentSnapshot(), s => s.Data.SequenceEqual(expectedAck));
        conn.Close();
    }

    [Fact(DisplayName = "PROTO-18+22 media records spanning many DRW frames reassemble into one packet")]
    public async Task KeyframeSizedRecordsReassemble()
    {
        var sockets = new FakeSockets();
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(PunchPacket(), Camera, 32108));
        sockets.Udp_.Incoming.Writer.TryWrite(new Datagram(ReadyTcp(), Camera, 32108));

        using var conn = new Cs2Conn(sockets);
        await conn.DialAsync(Camera, "tcp");

        // A keyframe-sized media record: 32-byte header + 3000 payload bytes, length-prefixed and
        // chopped into ~1KB DRW frames like the camera does.
        var inner = new byte[32 + 3000];
        for (var i = 0; i < inner.Length; i++)
        {
            inner[i] = (byte)i;
        }

        var record = new byte[4 + inner.Length];
        record.PutBeU32(0, inner.Length);
        Buffer.BlockCopy(inner, 0, record, 4, inner.Length);

        var seq = 0;
        var off = 0;
        while (off < record.Length)
        {
            var end = Math.Min(off + 1024, record.Length);
            sockets.Tcp_.Feed(Cs2.TcpFrame(DrwFrame(record.Slice(off, end), 2, seq++)));
            off = end;
        }

        var (hdr, body) = await WithTimeout(conn.ReadPacketAsync());
        Assert.Equal(inner.Slice(0, 32), hdr);
        Assert.Equal(3000, body.Length);
        Assert.Equal(inner.Slice(32, inner.Length), body);
        conn.Close();
    }
}
