using System.Net;
using System.Net.Sockets;

namespace BabyMonitor.Core.Net;

/// <summary>
/// Socket seams for the CS2 transport — real sockets in the app, scripted fakes in the tests.
///
/// Two properties every real implementation must have, and both are tested against the real sockets
/// (SocketsTest), never against a fake:
///
///  - WATCH-8: a read must be interruptible. A camera that never answers must not park the handshake
///    forever — no timeout could fire, no further attempt would be made, and the app would sit on
///    "Connecting…" all night, having silently stopped looking.
///  - WATCH-7: a read must give up on its own. A camera that goes quiet must not hold the read open
///    until TCP's own timeout, which is tens of minutes away.
/// </summary>
public sealed record Datagram(byte[] Data, string Host, int Port);

public interface IUdpSocket : IDisposable
{
    Task BindAsync();

    Task SendAsync(byte[] data, string host, int port, CancellationToken ct = default);

    Task<Datagram> ReceiveAsync(CancellationToken ct = default);

    void Close();
}

public interface ITcpSocket : IDisposable
{
    Task ConnectAsync(string host, int port, CancellationToken ct = default);

    Task WriteAsync(byte[] data, CancellationToken ct = default);

    Task<byte[]> ReadExactAsync(int n, CancellationToken ct = default);

    void Close();
}

public interface ISocketFactory
{
    IUdpSocket Udp();

    ITcpSocket Tcp();
}

public sealed class SocketClosedException : Exception
{
    public SocketClosedException(string message, Exception? cause = null)
        : base(message, cause)
    {
    }
}

public static class SocketTimeouts
{
    public const int ConnectMs = 5_000;
    public const int ReadMs = 15_000;
}

public sealed class SystemSocketFactory : ISocketFactory
{
    public static readonly SystemSocketFactory Shared = new();

    public IUdpSocket Udp() => new SystemUdpSocket();

    public ITcpSocket Tcp() => new SystemTcpSocket();
}

internal sealed class SystemUdpSocket : IUdpSocket
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly byte[] _buffer = new byte[65536];
    private bool _closed;

    public Task BindAsync()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return Task.CompletedTask;
    }

    public async Task SendAsync(byte[] data, string host, int port, CancellationToken ct = default)
    {
        if (_closed)
        {
            throw new SocketClosedException("udp: socket closed");
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
        await _socket.SendToAsync(data, SocketFlags.None, endpoint, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// WATCH-7/8: bounded and cancellable. A camera that stops answering fails the read rather than
    /// holding it open, and a cancellation (user stop, camera switch) takes effect at once.
    /// </summary>
    public async Task<Datagram> ReceiveAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SocketTimeouts.ReadMs);
        try
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var result = await _socket
                .ReceiveFromAsync(_buffer, SocketFlags.None, from, timeout.Token)
                .ConfigureAwait(false);
            var remote = (IPEndPoint)result.RemoteEndPoint;
            var data = new byte[result.ReceivedBytes];
            Buffer.BlockCopy(_buffer, 0, data, 0, result.ReceivedBytes);
            return new Datagram(data, remote.Address.ToString(), remote.Port);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SocketClosedException("udp: read timed out");
        }
        catch (ObjectDisposedException e)
        {
            throw new SocketClosedException("udp: socket closed", e);
        }
        catch (SocketException e)
        {
            throw new SocketClosedException($"udp: {e.Message}", e);
        }
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            _socket.Close();
        }
        catch (SocketException)
        {
            // Closing a socket that is already gone is not a failure worth telling anyone about.
        }
    }

    public void Dispose()
    {
        Close();
        _socket.Dispose();
    }
}

internal sealed class SystemTcpSocket : ITcpSocket
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private bool _closed;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SocketTimeouts.ConnectMs);
        try
        {
            _socket.NoDelay = true; // a monitor's frames are latency, not throughput
            await _socket.ConnectAsync(IPAddress.Parse(host), port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SocketClosedException($"tcp: connect to {host}:{port} timed out");
        }
        catch (SocketException e)
        {
            throw new SocketClosedException($"tcp: connect to {host}:{port} failed — {e.Message}", e);
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        if (_closed)
        {
            throw new SocketClosedException("tcp: socket closed");
        }

        try
        {
            var sent = 0;
            while (sent < data.Length)
            {
                sent += await _socket
                    .SendAsync(data.AsMemory(sent, data.Length - sent), SocketFlags.None, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            throw new SocketClosedException($"tcp: write failed — {e.Message}", e);
        }
    }

    /// <summary>WATCH-7/8: bounded and cancellable, for the same reasons as the UDP read above.</summary>
    public async Task<byte[]> ReadExactAsync(int n, CancellationToken ct = default)
    {
        var out_ = new byte[n];
        var read = 0;
        while (read < n)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SocketTimeouts.ReadMs);
            int got;
            try
            {
                got = await _socket
                    .ReceiveAsync(out_.AsMemory(read, n - read), SocketFlags.None, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SocketClosedException("tcp: read timed out");
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                throw new SocketClosedException($"tcp: read failed — {e.Message}", e);
            }

            if (got <= 0)
            {
                throw new SocketClosedException("tcp: connection closed by peer");
            }

            read += got;
        }

        return out_;
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            _socket.Close();
        }
        catch (SocketException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Close();
        _socket.Dispose();
    }
}
