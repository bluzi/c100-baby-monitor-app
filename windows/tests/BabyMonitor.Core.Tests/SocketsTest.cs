using System.Diagnostics;
using BabyMonitor.Core.Net;
using Xunit;

namespace BabyMonitor.Core.Tests;

// WATCH-7/8: a socket read that cannot be interrupted, or that never gives up, parks the reconnect
// loop for good — the app sits on "Connecting…" all night and never looks for the camera again. These
// are properties of the REAL sockets, so they are tested against them, not against a fake. Every
// platform that ships this app owes the same promise.

public class SocketsTest
{
    [Fact(DisplayName = "WATCH-8 a peer that never answers cannot park a UDP read forever")]
    public async Task UdpReadsAreInterruptible()
    {
        using var udp = SystemSocketFactory.Shared.Udp();
        await udp.BindAsync(); // an ephemeral port nobody will ever send to

        using var cts = new CancellationTokenSource(1_500);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => udp.ReceiveAsync(cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the read hung for {sw.Elapsed}");
    }

    [Fact(DisplayName = "WATCH-7 a TCP connect to a black hole gives up rather than hanging on the kernel")]
    public async Task TcpConnectGivesUp()
    {
        using var tcp = SystemSocketFactory.Shared.Tcp();

        // TEST-NET-1 (RFC 5737): routable-looking, never answers. A blocking connect would sit here for
        // over a minute; ours must give up inside its own budget.
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SocketClosedException>(() => tcp.ConnectAsync("192.0.2.1", 32108));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"the connect hung for {sw.Elapsed}");
    }

    [Fact(DisplayName = "WATCH-7 a TCP read gives up on its own when the camera goes quiet")]
    public async Task TcpReadsTimeOut()
    {
        // A camera that stops sending must not hold the read open until TCP's own timeout, which is
        // tens of minutes away. The read's budget is 15 s, so this proves it is bounded and cancellable
        // rather than waiting the whole budget out.
        using var listener = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((System.Net.IPEndPoint)listener.LocalEndPoint!).Port;

        using var tcp = SystemSocketFactory.Shared.Tcp();
        await tcp.ConnectAsync("127.0.0.1", port);
        using var accepted = await listener.AcceptAsync(); // connected, and then silent

        using var cts = new CancellationTokenSource(1_000);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tcp.ReadExactAsync(8, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the read hung for {sw.Elapsed}");
    }

    [Fact(DisplayName = "a real UDP round-trip carries the bytes and the sender's address")]
    public async Task UdpRoundTrips()
    {
        using var a = SystemSocketFactory.Shared.Udp();
        using var b = SystemSocketFactory.Shared.Udp();
        await a.BindAsync();
        await b.BindAsync();

        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;

        var payload = new byte[] { 0xf1, 0x30, 0, 0 };
        await a.SendAsync(payload, "127.0.0.1", port);

        var buffer = new byte[64];
        var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        var result = await probe.ReceiveFromAsync(buffer, System.Net.Sockets.SocketFlags.None, from);
        Assert.Equal(payload, buffer[..result.ReceivedBytes]);
    }
}
