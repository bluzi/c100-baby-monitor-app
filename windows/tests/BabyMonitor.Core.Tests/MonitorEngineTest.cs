using BabyMonitor.Core.Data;
using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Net;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The engine's lifecycle — the part a parent actually presses.
///
/// It is driven against a camera that never answers, which is the honest way to test a monitor: the
/// connection loop keeps retrying, so `Running` stays true and the states below are the ones a real
/// 3am stop/start lands in.
/// </summary>
public class MonitorEngineTest : IDisposable
{
    private readonly MemoryKv _kv = new();
    private readonly AppStore _store;
    private readonly RecordingRinger _ringer = new();
    private readonly MonitorEngine _engine;

    public MonitorEngineTest()
    {
        _store = new AppStore(_kv, new MarkingSecretBox());
        _store.SaveSession(new Session("U1", "CU1", "PT1", "ST1", new byte[16], "de"));
        _store.SaveDevice(new Device("did-1", "Nursery", "chuangmi.camera.077ac1", "AA:BB", "192.0.2.1"));

        // A camera that is not there. Every attempt fails, and the loop keeps trying — which is exactly
        // the state monitoring is in when the Wi-Fi is down and a parent goes to look.
        var http = new ScriptedHttp(_ => throw new SocketClosedException("http: the camera's cloud is unreachable"));
        _engine = new MonitorEngine(_store, _ringer, new NullMedia(), new NullSockets(), http);
    }

    public void Dispose()
    {
        _engine.Stop();
        MonitorHub.Running.Value = false;
        MonitorHub.Status.Value = Statuses.Idle;
        MonitorHub.ActiveAlarm.Value = null;
    }

    [Fact(DisplayName = "BG-11 monitoring can be started again the instant it is stopped — never a dead end")]
    public async Task StartAfterStopActuallyStarts()
    {
        // This is driven against a camera that answers — a full CS2 handshake and a MISS session — and
        // whose socket is SLOW to write, because that is what makes the bug it pins real: a stopped
        // connection loop keeps unwinding for as long as the goodbye to the camera takes, and a parent
        // who presses Stop and changes their mind lands inside exactly that window.
        //
        // If "already monitoring" were decided by the loop still existing rather than by whether we are
        // monitoring, Start would answer "already monitoring" and do nothing — and a half-asleep parent
        // would be looking at a Start button that does not start.
        var camera = new SlowCamera();
        var engine = new MonitorEngine(_store, _ringer, new NullMedia(), camera, LiveCameraHttp());

        engine.Start();
        await camera.WaitForSessionAsync(); // the handshake completed; the monitor is on the camera
        Assert.True(MonitorHub.Running.Value);

        engine.Stop();
        Assert.False(MonitorHub.Running.Value);
        Assert.Equal(Statuses.Stopped, MonitorHub.Status.Value);

        // Immediately — the connection loop is still inside its goodbye, which this socket makes slow.
        engine.Start();
        Assert.True(MonitorHub.Running.Value);

        // And it is really monitoring again: the loop is running and reporting, not sitting on "stopped".
        for (var i = 0; i < 200 && MonitorHub.Status.Value == Statuses.Stopped; i++)
        {
            await Task.Delay(20);
        }

        Assert.NotEqual(Statuses.Stopped, MonitorHub.Status.Value);
        engine.Stop();
    }

    [Fact(DisplayName = "BG-11 stopping silences a ringing alarm")]
    public void StoppingSilencesTheAlarm()
    {
        _engine.Start();
        MonitorHub.ActiveAlarm.Value = AlarmKind.BabyNoise;

        _engine.Stop();

        Assert.True(_ringer.Acknowledged > 0);
    }

    [Fact(DisplayName = "LIVE-5 a cloud that times out is retried — it never quietly ends the night")]
    public async Task ATimingOutCloudIsRetried()
    {
        // The failure this pins is the quietest one there is. .NET reports an HTTP timeout as a
        // TaskCanceledException, and the engine treats an OperationCanceledException as "the user
        // stopped monitoring" — so a single slow answer from Mi Cloud would unwind the reconnect loop
        // for good: still "running", still saying "connecting", never reconnecting, and never saying a
        // word about it. The monitor would look exactly like a monitor that was working.
        var engine = new MonitorEngine(
            _store,
            _ringer,
            new NullMedia(),
            new NullSockets(),
            new ScriptedHttp(_ => throw new IOException("http: GET … timed out"))); // what the client now raises

        engine.Start();

        for (var i = 0; i < 200; i++)
        {
            if (MonitorHub.Status.Value.StartsWith("reconnecting", StringComparison.Ordinal))
            {
                Assert.True(MonitorHub.Running.Value); // still monitoring, and still trying
                engine.Stop();
                return;
            }

            await Task.Delay(50);
        }

        engine.Stop();
        Assert.Fail($"a timed-out cloud was not retried (status: {MonitorHub.Status.Value})");
    }

    [Fact(DisplayName = "LIVE-5 a camera that never answers is retried, and the status says so")]
    public async Task ItKeepsTryingAndSaysSo()
    {
        _engine.Start();

        // WATCH-8: it must never park on "connecting" — the attempt fails and the countdown begins.
        for (var i = 0; i < 200; i++)
        {
            var status = MonitorHub.Status.Value;
            if (status.StartsWith("reconnecting", StringComparison.Ordinal) ||
                status.StartsWith("error:", StringComparison.Ordinal))
            {
                Assert.True(MonitorHub.Running.Value); // it is still monitoring; it just cannot reach it
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"the engine never reported a failed attempt (status: {MonitorHub.Status.Value})");
    }

    [Fact(DisplayName = "LIVE-18 changing the picture quality re-asks the camera — the feed reconnects")]
    public async Task ChangingQualityReconnectsTheFeed()
    {
        // The quality is chosen when the stream is asked for, so a parent who picks SD under a running
        // stream must get a NEW session — otherwise the control silently does nothing until the next
        // reconnect, which may be hours away. The camera cannot be asked to change its mind mid-stream.
        //
        // The ask itself is encrypted on the wire (MISS/ChaCha20), so what is observed here is the
        // session count; that the quality string maps to the right `videoquality` is PROTO-21's job.
        var camera = new RestartingCamera();
        var engine = new MonitorEngine(_store, _ringer, new NullMedia(), camera, LiveCameraHttp());

        engine.Start();
        await camera.WaitForSessionAsync(1);

        _store.SaveSettings(_store.LoadSettings() with { VideoQuality = Settings.QualitySd });
        MonitorHub.ApplySettings(_store.LoadSettings());

        // A second session, without anything having failed — this is the reconnect the control promises.
        await camera.WaitForSessionAsync(2);

        engine.Stop();
    }

    [Fact(DisplayName = "LIVE-18 choosing the quality already in use disturbs nothing")]
    public async Task ChoosingTheSameQualityDoesNotReconnect()
    {
        // Re-picking HD when HD is already playing must not drop the sound. A monitor that goes quiet
        // for no reason at all is worse than one that never offered the control.
        var camera = new RestartingCamera();
        var engine = new MonitorEngine(_store, _ringer, new NullMedia(), camera, LiveCameraHttp());

        engine.Start();
        await camera.WaitForSessionAsync(1);

        // Same quality, and a mute toggle beside it: settings do change, the picture's size does not.
        _store.SaveSettings(_store.LoadSettings() with { VideoQuality = Settings.QualityHd, Muted = true });
        MonitorHub.ApplySettings(_store.LoadSettings());
        await Task.Delay(300);

        Assert.Equal(1, camera.Sessions);
        engine.Stop();
    }

    /// <summary>One command record: [BE u32 len][LE u32 cmd][payload]. Shared by the camera fakes below.</summary>
    private static byte[] AuthOk()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("""{"result":"success"}""");
        var record = new byte[8 + payload.Length];
        record.PutBeU32(0, 4 + payload.Length);
        record.PutLeU32(4, 0x100);
        Buffer.BlockCopy(payload, 0, record, 8, payload.Length);
        return record;
    }

    private static byte[] Drw(byte[] chunk, int channel, int seq)
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

    /// <summary>A cloud that hands out a real camera: vendor cs2, a device key, a sign token.</summary>
    private static IMiHttp LiveCameraHttp()
    {
        var ssecurity = new byte[16];
        var http = new FakeMiHttp();
        http.Handler = req =>
        {
            if (req.Url.EndsWith("/v2/device/miss_get_vendor", StringComparison.Ordinal))
            {
                var devicePublic = new string('a', 64); // 32 bytes of curve point; any point will do
                var result =
                    "{\"code\":0,\"result\":{" +
                    "\"vendor\":{\"vendor\":4,\"vendor_params\":{\"p2p_id\":\"ABC\"}}," +
                    $"\"public_key\":\"{devicePublic}\",\"sign\":\"SIGN\"}}}}";
                return Http.Resp(body: Http.EncryptResponse(req, ssecurity, result));
            }

            // The device-list refresh is allowed to fail: the engine falls back to the stored address.
            throw new SocketClosedException("http: the device list is not part of this test");
        };
        return http;
    }

    /// <summary>
    /// A camera that will complete the CS2 handshake and the MISS session **as many times as it is
    /// asked to** — one fresh socket per session, each answering the auth. SlowCamera hands out the same
    /// pair for the life of the test, which is all a single session needs; a reconnect needs the camera
    /// to still be there the second time, which is exactly what LIVE-18 turns on.
    /// </summary>
    private sealed class RestartingCamera : ISocketFactory
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _waiters = new();
        private int _sessions;

        /// <summary>How many times the monitor has got as far as asking this camera for media.</summary>
        public int Sessions
        {
            get { lock (_gate) { return _sessions; } }
        }

        public IUdpSocket Udp()
        {
            var udp = new FakeUdp();
            // PROTO-15: the punch packet, then "use TCP".
            udp.Incoming.Writer.TryWrite(new Datagram(new byte[] { 0xf1, 0x41, 0, 4, 9, 9, 9, 9 }, "192.0.2.1", 32108));
            udp.Incoming.Writer.TryWrite(new Datagram(new byte[] { 0xf1, 0x43, 0, 0 }, "192.0.2.1", 32108));
            return udp;
        }

        public ITcpSocket Tcp() => new SessionTcp(this);

        /// <summary>Resolves once the camera has been asked for media <paramref name="n"/> times.</summary>
        public Task WaitForSessionAsync(int n)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_sessions >= n)
                {
                    return Task.CompletedTask;
                }

                _waiters.Add(tcs);
            }

            return WaitFor(n, tcs);
        }

        private async Task WaitFor(int n, TaskCompletionSource tcs)
        {
            while (true)
            {
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_sessions >= n)
                    {
                        return;
                    }

                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(tcs);
                }
            }
        }

        private void OnMediaAsked()
        {
            List<TaskCompletionSource> waiting;
            lock (_gate)
            {
                _sessions++;
                waiting = new List<TaskCompletionSource>(_waiters);
                _waiters.Clear();
            }

            foreach (var w in waiting)
            {
                w.TrySetResult();
            }
        }

        /// <summary>One session's socket: answers the auth, then counts the ask for media.</summary>
        private sealed class SessionTcp : ITcpSocket
        {
            private readonly System.Threading.Channels.Channel<byte> _incoming =
                System.Threading.Channels.Channel.CreateUnbounded<byte>();

            private readonly RestartingCamera _camera;
            private volatile bool _closed;
            private int _writes;

            public SessionTcp(RestartingCamera camera)
            {
                _camera = camera;
                // PROTO-20: the MISS session says yes.
                foreach (var b in Cs2.TcpFrame(Drw(AuthOk(), channel: 0, seq: 0)))
                {
                    _incoming.Writer.TryWrite(b);
                }
            }

            public Task ConnectAsync(string host, int port, CancellationToken ct = default) => Task.CompletedTask;

            public Task WriteAsync(byte[] data, CancellationToken ct = default)
            {
                // The first write is the auth request; the second is startMedia — by then the monitor is
                // on the camera and this session is real. (The rest are keepalives and the goodbye.)
                if (Interlocked.Increment(ref _writes) == 2)
                {
                    _camera.OnMediaAsked();
                }

                return Task.CompletedTask;
            }

            public async Task<byte[]> ReadExactAsync(int n, CancellationToken ct = default)
            {
                var buf = new byte[n];
                for (var i = 0; i < n; i++)
                {
                    buf[i] = await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
                }

                return buf;
            }

            public void Close()
            {
                // Closing is how the engine ends a session to re-ask at another quality: the reader must
                // come undone rather than block for ever, or the reconnect never happens.
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _incoming.Writer.TryComplete(new SocketClosedException("tcp: closed"));
            }

            public void Dispose() => Close();
        }
    }

    /// <summary>
    /// A camera that completes the CS2 handshake and the MISS session — and whose socket takes its time
    /// over a write, the way a real one does when the far end has gone away. That delay is the whole
    /// point: it is what keeps a stopped connection loop alive long enough for Start to land inside it.
    /// </summary>
    private sealed class SlowCamera : ISocketFactory
    {
        private readonly TaskCompletionSource _session = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FakeUdp _udp = new();
        private readonly SlowTcp _tcp;

        public SlowCamera()
        {
            _tcp = new SlowTcp(_session);

            // PROTO-15: the punch packet, then "use TCP".
            _udp.Incoming.Writer.TryWrite(new Datagram(new byte[] { 0xf1, 0x41, 0, 4, 9, 9, 9, 9 }, "192.0.2.1", 32108));
            _udp.Incoming.Writer.TryWrite(new Datagram(new byte[] { 0xf1, 0x43, 0, 0 }, "192.0.2.1", 32108));

            // PROTO-20: and the MISS session says yes.
            _tcp.Feed(Cs2.TcpFrame(Drw(AuthOk(), channel: 0, seq: 0)));
        }

        public IUdpSocket Udp() => _udp;

        public ITcpSocket Tcp() => _tcp;

        public Task WaitForSessionAsync() => _session.Task.WaitAsync(TimeSpan.FromSeconds(10));

        private sealed class SlowTcp : ITcpSocket
        {
            private readonly System.Threading.Channels.Channel<byte> _incoming =
                System.Threading.Channels.Channel.CreateUnbounded<byte>();

            private readonly TaskCompletionSource _session;
            private int _writes;

            public SlowTcp(TaskCompletionSource session) => _session = session;

            public void Feed(byte[] bytes)
            {
                foreach (var b in bytes)
                {
                    _incoming.Writer.TryWrite(b);
                }
            }

            public Task ConnectAsync(string host, int port, CancellationToken ct = default) => Task.CompletedTask;

            public async Task WriteAsync(byte[] data, CancellationToken ct = default)
            {
                // The second write is startMedia — by then the session is up and the monitor is on the
                // camera. (The first is the auth request, the rest are keepalives and the goodbye.)
                if (Interlocked.Increment(ref _writes) == 2)
                {
                    _session.TrySetResult();
                }

                // A write to a camera that has gone away does not fail; it waits. That is what makes the
                // goodbye in the engine's teardown slow, and it is the state this test exists to create.
                await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
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
    }

    private sealed class RecordingRinger : IRinger
    {
        public int Acknowledged { get; private set; }

        public bool Ring(AlarmKind kind, string cameraName)
        {
            MonitorHub.ActiveAlarm.Value = kind;
            return true;
        }

        public void Acknowledge()
        {
            Acknowledged++;
            MonitorHub.ActiveAlarm.Value = null;
        }
    }

    /// <summary>A media stack with nowhere to play: the engine never gets far enough to use it.</summary>
    private sealed class NullMedia : IMediaFactory
    {
        public IAudioOutput Audio(Action<short[], int> onPcmWindow) => new NullAudio();

        public IVideoOutput Video() => new NullVideo();

        private sealed class NullAudio : IAudioOutput
        {
            public bool Muted { get; set; }

            public void Start()
            {
            }

            public void Push(byte[] packet, long ptsMs)
            {
            }

            public void Release()
            {
            }
        }

        private sealed class NullVideo : IVideoOutput
        {
            public void Push(byte[] annexB, long ptsMs)
            {
            }

            public void Release()
            {
            }
        }
    }

    private sealed class NullSockets : ISocketFactory
    {
        public IUdpSocket Udp() => throw new SocketClosedException("udp: the camera is not there");

        public ITcpSocket Tcp() => throw new SocketClosedException("tcp: the camera is not there");
    }
}
