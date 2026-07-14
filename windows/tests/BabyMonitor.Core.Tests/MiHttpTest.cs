using System.Net;
using System.Net.Sockets;
using BabyMonitor.Core.Net;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The real HTTP client, against a real socket that accepts and then says nothing.
///
/// **A timeout must not look like a cancellation**, and this is the test that says so. .NET reports
/// HttpClient's own timeout as a TaskCanceledException — which *is* an OperationCanceledException, the
/// very thing the monitor uses to mean "the user stopped monitoring". Left alone, one slow answer from
/// Mi Cloud at 2am unwinds the reconnect loop for good: no retry, no error, no alarm, nothing in the
/// log but silence — and an app that goes on looking like it is watching.
///
/// On the JVM the same event is a SocketTimeoutException, and is simply retried. That is the behaviour
/// this pins.
/// </summary>
public class MiHttpTest
{
    [Fact(DisplayName = "LIVE-5+WATCH-8 a slow server is a retryable failure — never a cancellation")]
    public async Task ATimeoutIsNotACancellation()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        // Accept the connection and then never answer — a black hole, which is what a router doing
        // this to a camera's cloud looks like from here.
        var accepting = listener.AcceptAsync();

        using var http = new SystemMiHttp(timeoutMs: 300);
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => http.RequestAsync($"http://127.0.0.1:{port}/pass/serviceLogin"));

        Assert.IsNotType<OperationCanceledException>(error);
        Assert.IsAssignableFrom<IOException>(error);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);

        if (accepting.IsCompletedSuccessfully)
        {
            accepting.Result.Dispose();
        }
    }

    [Fact(DisplayName = "AUTH-10 a cancellation is still a cancellation — a stop stops")]
    public async Task ACancellationIsStillACancellation()
    {
        // The other half: when the *user* stops monitoring, the request must cancel, and the engine
        // must be able to tell that apart from a camera that is merely slow.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var accepting = listener.AcceptAsync();

        using var http = new SystemMiHttp(timeoutMs: 30_000);
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => http.RequestAsync($"http://127.0.0.1:{port}/pass/serviceLogin", ct: cts.Token));

        if (accepting.IsCompletedSuccessfully)
        {
            accepting.Result.Dispose();
        }
    }
}
