using System.Threading.Channels;
using BabyMonitor.Core.Data;
using BabyMonitor.Core.Dsp;
using BabyMonitor.Core.Logging;
using BabyMonitor.Core.Net;
using BabyMonitor.Core.Platform;
using BabyMonitor.Core.Xiaomi;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// The monitor itself: Mi Cloud key exchange → MISS connect → decode loop, with automatic reconnect
/// (LIVE-5/BG-6), the noise alarm (ALRM) and the feed watchdog (WATCH).
///
/// Platform-free: it is handed a <see cref="IRinger"/> and a <see cref="IMediaFactory"/> and knows
/// nothing else about the machine it runs on. That is what lets one monitor serve every app — the
/// phone, the Mac and the PC run the same logic, and the same spec suite guards it on all three.
///
/// Owned by whatever keeps it alive (the tray-resident process on Windows); state surfaces through
/// <see cref="MonitorHub"/>.
/// </summary>
public sealed class MonitorEngine
{
    private const long AlarmCooldownMs = 30_000; // ALRM-5
    private const int ConnectTimeoutMs = 25_000; // a camera that never finishes the handshake
    private const int FirstFrameTimeoutMs = 10_000; // a camera that accepts the session, then sends nothing

    /// <summary>
    /// LIVE-12: this many audio frames in a codec we cannot play, with no opus at all, means the
    /// camera's audio format is unsupported — permanent, not a connection blip worth retrying.
    /// </summary>
    private const int UnsupportedAudioGiveUp = 50;

    private readonly AppStore _store;
    private readonly IRinger _ringer;
    private readonly IMediaFactory _media;
    private readonly ISocketFactory _sockets;
    private readonly IMiHttp _http;
    private readonly BabyNoiseDetector _detector = new();
    private readonly StreamWatchdog _watchdog = new();

    private CancellationTokenSource? _auxCts;
    private CancellationTokenSource? _connCts;
    private Task? _connTask;
    private Task? _watchdogTask;

    private volatile IAudioOutput? _player;
    private volatile IVideoOutput? _renderer;
    private volatile MissClient? _client;
    private volatile AlarmSchedule _schedule = new(Windowed: false);

    private LevelMeter _meter = new();

    /// <summary>ALRM-16: learned steps for the current camera, applied on top of the sensitivity dial.</summary>
    private volatile int _calibrationSteps;

    // Room-level telemetry (LIVE-6): only touched from the audio window callback.
    private List<double> _levelStats = new(2048);
    private long _levelStatsStartMs;

    /// <summary>Keeps a slept-through alarm from writing a log line every second all night (WATCH-6).</summary>
    private volatile bool _suppressionLogged;

    /// <summary>ALRM-15: the camera that was ringing, captured at ring time (a switch may follow).</summary>
    private volatile string? _alarmDid;

    public MonitorEngine(
        AppStore store,
        IRinger ringer,
        IMediaFactory media,
        ISocketFactory? sockets = null,
        IMiHttp? http = null)
    {
        _store = store;
        _ringer = ringer;
        _media = media;
        _sockets = sockets ?? SystemSocketFactory.Shared;
        _http = http ?? SystemMiHttp.Shared;
    }

    /// <summary>Monotonic, deliberately: see <see cref="Clock.ElapsedRealtimeMs"/>.</summary>
    private static long NowMs() => Clock.ElapsedRealtimeMs();

    public void Start()
    {
        // BG-11: `Running` — not the task — is what "already monitoring" means. A stopped monitor's
        // connection loop is still unwinding for a moment (the goodbye to the camera is given a whole
        // second), and a parent who presses Stop and changes their mind lands inside exactly that
        // moment. Keying this off the task alone would answer "already monitoring" and do nothing,
        // leaving them looking at a Start button that does not start. The loop below still waits for
        // that teardown before it touches anything.
        if (_connTask is { IsCompleted: false } && MonitorHub.Running.Value)
        {
            if (MonitorHub.Status.Value != Statuses.MonitorFailed)
            {
                Log.I("engine", "start ignored — already monitoring");
                return;
            }

            // WATCH-11/APP-3: an aux loop (the watchdog) died while the connection loop lived on.
            // "Press Resume to restart" must actually restart — a monitor without its watchdog is
            // broken even if audio still flows, so tear the survivor down too.
            Log.W("engine", "restarting after a monitor failure — cancelling the surviving loops");
        }

        Log.I("engine", "start monitoring");

        // The connection loop can return on its own (an expired session), leaving the aux loops
        // running. Starting again must not stack a second watchdog tick loop on top of the live one.
        CancelAux();

        MonitorHub.Running.Value = true;
        MonitorHub.SessionExpired.Value = false;
        MonitorHub.LastAudioAtMs = 0;
        MonitorHub.ApplySettings(_store.LoadSettings());
        MonitorHub.OnAcknowledge = AcknowledgeAlarm;

        // ALRM-16/17: this camera's learned tuning, loaded with clamped trust in what's stored.
        var did = _store.LoadDevice()?.Did;
        _calibrationSteps = did == null
            ? 0
            : Math.Clamp(_store.CryCalibrationSteps(did), 0, CryCalibration.MaxSteps);
        MonitorHub.CalibrationSteps.Value = _calibrationSteps;
        MonitorHub.OnCryFeedback = ApplyCryFeedback;
        MonitorHub.OnCalibrationReset = ResetCalibration;
        _store.SetMonitoring(true); // BG-13w: so a restart can be reported

        _auxCts = new CancellationTokenSource();
        var auxToken = _auxCts.Token;

        // The settings mirror, applied now and on every change (ALRM-2: effective immediately).
        ApplySettings(MonitorHub.Settings.Value);
        MonitorHub.Settings.Changed -= OnSettingsChanged;
        MonitorHub.Settings.Changed += OnSettingsChanged;

        // WATCH-2/7: watch the feed itself, whatever the cause of silence.
        _watchdogTask = Announce(Task.Run(() => WatchdogLoopAsync(auxToken), auxToken), "watchdog tick loop");

        // Never overlap two connection loops on one engine: the old loop's teardown writes the shared
        // client/player/renderer fields, and racing it could release the new connection's objects.
        var previousTask = _connTask;
        var previousCts = _connCts;
        _connCts = new CancellationTokenSource();
        var connToken = _connCts.Token;
        _connTask = Announce(
            Task.Run(
                async () =>
                {
                    if (previousTask != null)
                    {
                        previousCts?.Cancel();
                        try
                        {
                            await previousTask.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Whatever killed the old loop was already reported by it.
                        }
                    }

                    await ConnectionLoopAsync(connToken).ConfigureAwait(false);
                },
                CancellationToken.None),
            "connection loop");
    }

    public void Stop()
    {
        Log.I("engine", "stop monitoring");
        MonitorHub.Running.Value = false;
        MonitorHub.Status.Value = Statuses.Stopped;

        // A stop closes the expiry story too: a stale flag left set here would sign the user out again
        // the next time the viewer opens.
        MonitorHub.SessionExpired.Value = false;
        MonitorHub.Level.Value = 0;

        // The BG-13w restart marker is NOT cleared here: Stop() also runs on system-initiated teardown
        // and camera switches. Only the user-intent paths — the Stop action and signing out — clear it.
        _ringer.Acknowledge(); // stopping monitoring silences any ringing alarm
        _watchdog.Reset();
        CancelAux();
        _connCts?.Cancel();
    }

    private static Task Announce(Task task, string what)
    {
        // WATCH-11: any piece of the monitor dying abnormally must be announced, never shown as
        // "retrying". The monitor may fail; it may never fail silently.
        _ = task.ContinueWith(
            t =>
            {
                if (!t.IsFaulted)
                {
                    return;
                }

                var cause = t.Exception?.GetBaseException();
                if (cause is OperationCanceledException)
                {
                    return;
                }

                Log.E("engine", $"{what} died — monitoring is no longer working: {cause?.Message}", cause);
                MonitorHub.Status.Value = Statuses.MonitorFailed;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private void CancelAux()
    {
        MonitorHub.Settings.Changed -= OnSettingsChanged;
        _auxCts?.Cancel();
        _auxCts = null;
        _watchdogTask = null;
    }

    private void OnSettingsChanged(Settings s)
    {
        // A throw here would kill the subscription for the life of the engine: the user would turn the
        // alarm on, see it on, and have nothing arm. Never let that happen.
        try
        {
            ApplySettings(s);
        }
        catch (Exception e)
        {
            Log.W("engine", $"could not apply settings: {e.Message}", e);
        }
    }

    private void ApplySettings(Settings s)
    {
        var player = _player;
        if (player != null)
        {
            player.Muted = s.Muted;
        }

        _detector.Enabled = s.AlarmEnabled;
        // ALRM-2/16: the dial plus this camera's learned steps set the loudness bar.
        _detector.ThresholdDb = CryCalibration.EffectiveThresholdDb(s.AlarmSensitivity, _calibrationSteps);
        _schedule = AlarmSchedule.From(s);
        // watchdog.Enabled is set by the tick loop: arming depends on the wall clock.
        _watchdog.GraceMs = s.WatchdogGraceSeconds * 1000L;
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (!MonitorHub.Running.Value)
            {
                _watchdog.Reset();
                continue;
            }

            var now = NowMs();
            var status = MonitorHub.Status.Value;
            var lastAudio = MonitorHub.LastAudioAtMs;

            // WATCH-7: a "live" connection that has gone quiet never recovers by itself — the read just
            // blocks. Force it closed so the normal reconnect path takes over.
            if (FeedLiveness.FeedStalled(status, lastAudio, now))
            {
                Log.W("engine", $"feed stalled (no audio for {now - lastAudio}ms) — dropping the connection");
                MonitorHub.Status.Value = "error: the camera stopped sending audio";
                try
                {
                    _client?.Close();
                }
                catch (Exception e)
                {
                    Log.W("engine", $"could not close the stalled connection: {e.Message}");
                }
            }

            // WATCH-9: armed only while the crying alarm could itself ring (wall clock, like ALRM-7 —
            // "only at night" is a statement about the wall clock).
            var s = MonitorHub.Settings.Value;
            _watchdog.Enabled = StreamWatchdog.Armed(
                s.WatchdogEnabled,
                s.AlarmEnabled,
                _schedule,
                Clock.WallClockMinutesOfDay());

            if (!_watchdog.OnTick(FeedLiveness.FeedAlive(status, lastAudio, now), now))
            {
                continue;
            }

            // WATCH-6: if another alarm is sounding, take the fire back and retry later rather than lose
            // the feed-down alarm for this outage entirely.
            if (_ringer.Ring(AlarmKind.FeedDown, MonitorHub.CameraName.Value))
            {
                Log.W("engine", "watchdog: feed down past grace period — alarm");
                _suppressionLogged = false;
            }
            else
            {
                _watchdog.Unfire(); // retried every tick until it can actually be heard
                if (!_suppressionLogged)
                {
                    _suppressionLogged = true;
                    Log.W("engine", "watchdog alarm suppressed (another alarm ringing) — retrying");
                }
            }
        }
    }

    private void AcknowledgeAlarm()
    {
        // Read what is ringing before acknowledge clears it — the answer belongs to THIS alarm.
        var acknowledged = MonitorHub.ActiveAlarm.Value;
        _ringer.Acknowledge();
        _detector.Suppressed = false;
        if (acknowledged == AlarmKind.BabyNoise)
        {
            // ALRM-5: the cooldown is the crying alarm's own. Acknowledging a feed-drop alarm must not
            // snooze cry detection — the feed may recover with the baby crying.
            _detector.Snooze(NowMs() + AlarmCooldownMs);
        }

        // The watchdog stays quiet for this outage by itself; recovery re-arms it (WATCH-3).

        // ALRM-15: a crying alarm earns the question; the answer is pinned to the camera that alarmed,
        // so it still lands right if the user switches cameras before answering.
        if (acknowledged != null && CryCalibration.AsksForCryFeedback(acknowledged.Value))
        {
            var did = _alarmDid ?? _store.LoadDevice()?.Did;
            if (did != null)
            {
                MonitorHub.PendingCryFeedback.Value = did;
            }
        }

        _alarmDid = null;
    }

    /// <summary>ALRM-16: one yes/no from the parent moves this camera's learned tuning one step.</summary>
    private void ApplyCryFeedback(string did, bool wasCry)
    {
        var stored = Math.Clamp(_store.CryCalibrationSteps(did), 0, CryCalibration.MaxSteps);
        var steps = wasCry ? CryCalibration.AfterRealCry(stored) : CryCalibration.AfterFalseAlarm(stored);
        _store.SaveCryCalibrationSteps(did, steps); // ALRM-17: persists
        Log.I(
            "engine",
            $"cry feedback for {did}: {(wasCry ? "real cry" : "false alarm")} → {steps} step(s) above the dial");
        ApplyCalibrationIfCurrent(did, steps);
    }

    /// <summary>ALRM-17: forget the current camera's learned tuning — back to the dial alone.</summary>
    private void ResetCalibration()
    {
        var did = _store.LoadDevice()?.Did;
        if (did == null)
        {
            return;
        }

        _store.SaveCryCalibrationSteps(did, 0);
        Log.I("engine", $"cry calibration reset for {did}");
        ApplyCalibrationIfCurrent(did, 0);
    }

    private void ApplyCalibrationIfCurrent(string did, int steps)
    {
        if (did != _store.LoadDevice()?.Did)
        {
            return; // feedback for a camera we've moved away from
        }

        _calibrationSteps = steps;
        MonitorHub.CalibrationSteps.Value = steps;
        // Effective immediately (ALRM-16), like every other alarm setting (ALRM-2).
        _detector.ThresholdDb = CryCalibration.EffectiveThresholdDb(
            MonitorHub.Settings.Value.AlarmSensitivity,
            steps);
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested && MonitorHub.Running.Value)
        {
            try
            {
                await ConnectOnceAsync(ct).ConfigureAwait(false);
                attempt = 0; // a successful session resets the backoff (LIVE-5)
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AuthExpiredException e)
            {
                // BG-8: retrying is pointless — say so and stop. Monitoring stays "running" so the
                // watchdog still alarms: an expired session at 3am means the baby is unmonitored.
                Log.W("engine", "session expired — stopping reconnects until the user signs in", e);
                MonitorHub.Status.Value = Statuses.SessionExpired;
                MonitorHub.SessionExpired.Value = true;
                return;
            }
            catch (UnsupportedCameraException e)
            {
                // LIVE-12: retrying can never fix this — say so instead of looping "connection lost"
                // forever. Monitoring stays "running" so the status is shown and the watchdog still
                // guards armed hours (WATCH-2).
                Log.W("engine", $"unsupported camera — stopping reconnects: {e.Message}");
                MonitorHub.Status.Value = Statuses.UnsupportedCamera;
                return;
            }
            catch (Exception e)
            {
                Log.W("engine", $"connection ended: {e.Message}", e);
                MonitorHub.Status.Value = $"error: {e.Message}";
            }

            if (!MonitorHub.Running.Value)
            {
                return;
            }

            var wait = Backoff.ReconnectDelayMs(attempt);
            attempt++;
            Log.I("engine", $"reconnecting in {wait}ms (attempt {attempt})");

            // LIVE-4: a live countdown, ticked once a second, not a frozen message.
            var remaining = wait;
            while (remaining > 0 && MonitorHub.Running.Value)
            {
                MonitorHub.Status.Value = Statuses.ReconnectStatus(remaining);
                var step = Math.Min(1000L, remaining);
                await Task.Delay((int)step, ct).ConfigureAwait(false);
                remaining -= step;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var session = _store.LoadSession();
        if (session == null)
        {
            // An empty store means the user signed out mid-flight (AUTH-10) — a deliberate stop, never
            // an expiry. Declaring it expired would poison the expired flag and bounce the *next*
            // sign-in straight back out.
            Log.I("engine", "no stored session — signed out; stopping monitoring");
            _store.SetMonitoring(false);
            MonitorHub.Running.Value = false;
            MonitorHub.Status.Value = Statuses.Stopped;
            return;
        }

        var device = _store.LoadDevice() ?? throw new XiaomiException("no camera selected");
        Log.I("engine", $"connecting to {device.Name} did={device.Did} model={device.Model} ip={device.Ip}");
        MonitorHub.CameraName.Value = device.Name;
        MonitorHub.Status.Value = Statuses.Connecting;

        var cloud = new MiCloud(_http, session: session)
        {
            OnSessionRefreshed = s =>
            {
                _store.SaveSession(s); // AUTH-7
                return Task.CompletedTask;
            },
        };

        // Refresh the camera's LAN address in case DHCP moved it since last time.
        try
        {
            var devices = await cloud.DeviceListAsync(ct).ConfigureAwait(false);
            var fresh = devices.FirstOrDefault(d => d.Did == device.Did);
            if (fresh != null && fresh.Ip.Length > 0 && fresh != device)
            {
                Log.I("engine", $"camera address updated: {device.Ip} -> {fresh.Ip}");
                device = fresh;
                _store.SaveDevice(fresh);
            }
        }
        catch (AuthExpiredException)
        {
            throw; // BG-8: a dead session is not a "using the stored ip" situation
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.W("engine", $"device-list refresh failed (using stored ip {device.Ip}): {e.Message}");
        }

        if (device.Ip.Length == 0)
        {
            Log.W("engine", "camera has no LAN ip — is it online and on the same network?");
        }

        var (pub, priv) = Crypto.GenerateBoxKeyPair();
        var vendor = await cloud.MissGetVendorAsync(device.Did, pub, ct).ConfigureAwait(false);
        if (Mi.VendorName(vendor.Vendor) != "cs2")
        {
            throw new UnsupportedCameraException(
                $"this camera speaks '{Mi.VendorName(vendor.Vendor)}' — only cs2 is supported");
        }

        var missClient = new MissClient(device.Model, _sockets);
        _client = missClient;

        // Everything from here on must be released on EVERY exit path. A CS2 connection owns its own
        // socket and a 1 Hz keepalive task that only Close() ever stops — leaking one per failed
        // handshake would, over a night of retries, exhaust the machine's handles and kill the monitor
        // silently.
        try
        {
            // WATCH-7: a camera that accepts the socket but never finishes the handshake must not leave
            // us stuck on "Connecting…" forever.
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(ConnectTimeoutMs);
                try
                {
                    await missClient.ConnectAsync(
                        new MissClient.ConnectParams(
                            Ip: device.Ip,
                            Vendor: "cs2",
                            ClientPublic: pub,
                            ClientPrivate: priv,
                            DevicePublicHex: vendor.DevicePublicHex,
                            Sign: vendor.Sign,
                            Transport: "tcp"),
                        connect.Token).ConfigureAwait(false);
                    await missClient.StartMediaAsync("hd", true, connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new XiaomiException("the camera did not answer in time");
                }
            }

            var transport = missClient.Conn.IsTcp ? "cs2+tcp" : "cs2+udp";
            Log.I("engine", $"media requested from {device.Name} ({device.Ip}) via {transport}");

            // WATCH-7: the session being up is not the feed being live. A camera that is switched off
            // still completes this handshake; only audio proves the feed. Stay "connecting".
            MonitorHub.LastAudioAtMs = 0;
            MonitorHub.Status.Value = Statuses.Connecting;

            _meter = new LevelMeter(); // fresh baseline per connection
            var audioPlayer = _media.Audio(OnPcmWindow);

            // Publish before configuring or starting: a mute toggle landing between snapshot and publish
            // would be lost for the whole connection, and if Start() throws the teardown must still
            // release the half-built decoder. Leaking one per retry would exhaust the machine's audio
            // clients — and a machine with none left has no audio, no level meter and no noise alarm.
            _player = audioPlayer;
            audioPlayer.Muted = MonitorHub.Settings.Value.Muted;
            audioPlayer.Start();

            // IVideoOutput.Push never throws — video trouble never takes audio down (LIVE-7).
            var videoRenderer = _media.Video();
            _renderer = videoRenderer;

            await PumpAsync(missClient, audioPlayer, videoRenderer, device, transport, ct).ConfigureAwait(false);
        }
        finally
        {
            // Runs on every path out — handshake failure, decoder failure, stall, cancellation. On
            // cancellation (user stop, camera switch) a plain awaited call would throw before sending
            // anything — give stopMedia a bounded window of its own so the camera actually hears it.
            try
            {
                using var goodbye = new CancellationTokenSource(1000);
                await missClient.StopMediaAsync(goodbye.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The camera will time the session out by itself. Never let a goodbye hold up teardown.
            }

            try
            {
                missClient.Close();
            }
            catch (Exception e)
            {
                Log.W("engine", $"could not close the camera connection: {e.Message}");
            }

            _client = null;

            try
            {
                _player?.Release();
            }
            catch (Exception e)
            {
                Log.W("engine", $"could not release the audio output: {e.Message}");
            }

            try
            {
                _renderer?.Release();
            }
            catch (Exception e)
            {
                Log.W("engine", $"could not release the video output: {e.Message}");
            }

            _player = null;
            _renderer = null;
        }
    }

    /// <summary>
    /// LIVE-8: the reader never blocks on decoding or playback, and audio and video are consumed
    /// independently, each dropping backlog rather than accumulating delay.
    /// </summary>
    private async Task PumpAsync(
        MissClient missClient,
        IAudioOutput audioPlayer,
        IVideoOutput videoRenderer,
        Device device,
        string transport,
        CancellationToken ct)
    {
        var audioQueue = Channel.CreateBounded<Frame.Audio>(new BoundedChannelOptions(VideoCatchup.AudioMaxBacklogPackets)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        var videoQueue = Channel.CreateUnbounded<Frame.Video>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var videoBacklog = 0;

        // LIVE-12: audio frames seen in a codec we cannot play, while no opus has arrived.
        var unsupportedAudio = 0;

        using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var audioTask = Task.Run(
            async () =>
            {
                await foreach (var f in audioQueue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    // A failing audio path must throw rather than play silence: a parent must never
                    // mistake a broken speaker for a quiet room. This kills the connection, and the
                    // engine rebuilds it.
                    audioPlayer.Push(f.Data, f.Pts);
                }
            },
            CancellationToken.None);

        var videoTask = Task.Run(
            async () =>
            {
                var catchup = new VideoCatchup();
                await foreach (var f in videoQueue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var backlog = Interlocked.Decrement(ref videoBacklog);
                    if (catchup.Admit(Hevc.IsKeyframe(f.Data), backlog))
                    {
                        videoRenderer.Push(f.Data, f.Pts);
                    }
                }
            },
            CancellationToken.None);

        // WATCH-7: if no audio ever arrives, the read below would block forever. Give the camera a
        // bounded chance to send its first audio, then drop and reconnect.
        var firstFrameGuard = Task.Run(
            async () =>
            {
                await Task.Delay(FirstFrameTimeoutMs, inner.Token).ConfigureAwait(false);
                if (MonitorHub.LastAudioAtMs != 0L)
                {
                    return;
                }

                // LIVE-12: any unsupported audio and no opus by the deadline is the same verdict as the
                // frame-count give-up — a slow camera must not evade it into the reconnect loop.
                if (Volatile.Read(ref unsupportedAudio) > 0)
                {
                    throw new UnsupportedCameraException("the camera's audio format isn't supported");
                }

                Log.W("engine", $"no audio within {FirstFrameTimeoutMs}ms — dropping the connection");
                MonitorHub.Status.Value = "error: the camera sent no audio";
                try
                {
                    missClient.Close();
                }
                catch (Exception e)
                {
                    Log.W("engine", $"could not close the silent connection: {e.Message}");
                }
            },
            inner.Token);

        var readerTask = Task.Run(
            async () =>
            {
                var sawVideo = false;
                var unsupportedVideoLogged = false;
                while (!inner.Token.IsCancellationRequested && MonitorHub.Running.Value)
                {
                    var frame = await missClient.ReadFrameAsync(inner.Token).ConfigureAwait(false);
                    switch (frame)
                    {
                        case Frame.Audio audio when audio.Codec == "opus":
                            if (MonitorHub.LastAudioAtMs == 0L)
                            {
                                // LIVE-4 / WATCH-7: only audio proves the feed. Video may already be
                                // rendering — it never flips us live by itself.
                                Log.I(
                                    "engine",
                                    $"LIVE: {device.Name} ({device.Ip}) via {transport} (opus {audio.SampleRate}Hz)");
                                MonitorHub.Status.Value = Statuses.Live;
                            }

                            MonitorHub.LastAudioAtMs = NowMs();
                            audioQueue.Writer.TryWrite(audio); // bounded + drop-oldest: never blocks
                            break;

                        case Frame.Audio audio when MonitorHub.LastAudioAtMs == 0L:
                        {
                            // LIVE-12: audio we cannot decode can never be monitored — a camera sending
                            // only such audio is permanent, not a blip.
                            var seen = Interlocked.Increment(ref unsupportedAudio);
                            if (seen == 1)
                            {
                                Log.W("engine", $"unsupported audio codec {audio.Codec} — cannot monitor with it");
                            }

                            if (seen >= UnsupportedAudioGiveUp)
                            {
                                throw new UnsupportedCameraException(
                                    $"the camera sends {audio.Codec} audio, which isn't supported");
                            }

                            break;
                        }

                        case Frame.Video video when video.Codec == "h265":
                            if (!sawVideo)
                            {
                                sawVideo = true;
                                Log.I("engine", "first video frame (h265)");
                            }

                            Interlocked.Increment(ref videoBacklog);
                            videoQueue.Writer.TryWrite(video);
                            break;

                        case Frame.Video video when !unsupportedVideoLogged:
                            // A camera sending only e.g. h264 shows a black picture; never let that be a
                            // silent mystery. Audio monitoring is unaffected (LIVE-7), so this is a log,
                            // not a failure.
                            unsupportedVideoLogged = true;
                            Log.W(
                                "engine",
                                $"unsupported video codec {video.Codec} — no picture; audio monitoring continues");
                            break;
                    }
                }
            },
            CancellationToken.None);

        try
        {
            // Whichever of these ends first decides the connection's fate: a reader that hit the end of
            // the stream, a guard that found no audio, or an audio path that failed. Awaiting the
            // finished one rethrows what it threw.
            var pending = new List<Task> { readerTask, firstFrameGuard, audioTask, videoTask };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                if (finished == readerTask)
                {
                    break;
                }

                pending.Remove(finished);
            }
        }
        finally
        {
            await inner.CancelAsync().ConfigureAwait(false); // never let the guard hold up teardown

            // Closing the queues lets both consumers drain and finish.
            audioQueue.Writer.TryComplete();
            videoQueue.Writer.TryComplete();
            foreach (var task in new[] { audioTask, videoTask, firstFrameGuard, readerTask })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already reported by whoever we are unwinding for.
                }
            }
        }
    }

    /// <summary>
    /// One compact room-level line every 30 s, so a night can be reconstructed from the log: was the
    /// room quiet, what stood out, where the ambient floor sat. A quiet room should read "median 0.0" —
    /// anything else is either a real event or a metering bug (LIVE-6).
    /// </summary>
    private void LogRoomLevel(double levelDb, long nowMs)
    {
        if (_levelStatsStartMs == 0L)
        {
            _levelStatsStartMs = nowMs;
        }

        _levelStats.Add(levelDb);
        if (nowMs - _levelStatsStartMs < 30_000)
        {
            return;
        }

        var sorted = _levelStats.ToArray();
        Array.Sort(sorted);
        Log.D(
            "engine",
            $"room level last {(nowMs - _levelStatsStartMs) / 1000}s: " +
            $"median {Format.OneDecimal(sorted[sorted.Length / 2])} max {Format.OneDecimal(sorted[^1])} " +
            $"dB above ambient (floor {Format.OneDecimal(_meter.FloorDb)} dBFS, {sorted.Length} windows)");

        _levelStats = new List<double>(2048);
        _levelStatsStartMs = nowMs;
    }

    private void OnPcmWindow(short[] pcm, int sampleRate)
    {
        var now = NowMs();
        var metrics = Analysis.AnalyzeWindow(pcm, sampleRate);
        var level = _meter.Process(metrics.Rms, metrics.Peak, now);
        MonitorHub.Level.Value = level;
        LogRoomLevel(level, now);

        var windowMs = pcm.Length * 1000L / sampleRate;
        if (!_detector.OnWindow(level, metrics, windowMs, now))
        {
            return;
        }

        // ALRM-7: only ring inside the armed window (wall clock, deliberately).
        if (!_schedule.IsActive(Clock.WallClockMinutesOfDay()))
        {
            return;
        }

        // ALRM-5 / WATCH-6: only suppress further triggers if the alarm actually started.
        if (_ringer.Ring(AlarmKind.BabyNoise, MonitorHub.CameraName.Value)) // ALRM-4
        {
            _detector.Suppressed = true; // nothing new until acknowledged
            _alarmDid = _store.LoadDevice()?.Did; // ALRM-15: pin the camera that alarmed
        }
    }
}
