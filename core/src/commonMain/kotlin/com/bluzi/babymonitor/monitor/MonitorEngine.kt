package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.AppStore
import com.bluzi.babymonitor.dsp.analyzeWindow
import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.net.platformHttp
import com.bluzi.babymonitor.net.platformSockets
import com.bluzi.babymonitor.platform.elapsedRealtimeMs
import com.bluzi.babymonitor.platform.wallClockMinutesOfDay
import com.bluzi.babymonitor.xiaomi.AuthExpiredException
import com.bluzi.babymonitor.xiaomi.Crypto
import com.bluzi.babymonitor.xiaomi.Frame
import com.bluzi.babymonitor.xiaomi.MiCloud
import com.bluzi.babymonitor.xiaomi.MissClient
import com.bluzi.babymonitor.xiaomi.UnsupportedCameraException
import com.bluzi.babymonitor.xiaomi.XiaomiException
import com.bluzi.babymonitor.xiaomi.vendorName
import kotlin.concurrent.Volatile
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull

private const val ALARM_COOLDOWN_MS = 30_000L // ALRM-5
private const val CONNECT_TIMEOUT_MS = 25_000L // a camera that never finishes the handshake
private const val FIRST_FRAME_TIMEOUT_MS = 10_000L // a camera that accepts the session, then sends nothing
// LIVE-12: this many audio frames in a codec we cannot play, with no opus at all, means the
// camera's audio format is unsupported — permanent, not a connection blip worth retrying.
private const val UNSUPPORTED_AUDIO_GIVE_UP = 50

/**
 * Monotonic clock. Everything the monitor times — outage length, alarm sustain, cooldown — must
 * be immune to the wall clock jumping (NTP resync, DST); a backwards jump at 3am would otherwise
 * hide an outage and mute the detector. Only the alarm *schedule* (ALRM-7) uses wall-clock time,
 * because "only at night" is a statement about the wall clock.
 */
private fun nowMs(): Long = elapsedRealtimeMs()

/**
 * The monitor itself: Mi Cloud key exchange → MISS connect → decode loop, with automatic
 * reconnect (LIVE-5/BG-6), the noise alarm (ALRM) and the feed watchdog (WATCH).
 *
 * Platform-free: it is handed a [Ringer] and a [MediaFactory] and knows nothing else about the
 * device it runs on. That is what lets one monitor serve every app — the phone and the Mac run
 * this same code, and the tests that guard it run on both.
 *
 * Owned by whatever keeps it alive (a foreground service on Android, the tray process on macOS);
 * state surfaces through MonitorHub.
 */
class MonitorEngine(
    private val store: AppStore,
    private val scope: CoroutineScope,
    private val ringer: Ringer,
    private val media: MediaFactory,
) {
    // Written by the connection coroutine (on expiry), read from the main thread in start()/stop().
    @Volatile
    private var job: Job? = null
    private var auxJobs = mutableListOf<Job>()

    // Written by the connection coroutine, read by the settings collector and the tick loop.
    @Volatile
    private var player: AudioOutput? = null

    @Volatile
    private var renderer: VideoOutput? = null

    @Volatile
    private var meter = LevelMeter()
    private val detector = BabyNoiseDetector()
    private val watchdog = StreamWatchdog()

    /** The connection in flight, so a stalled feed can be forced closed from the tick loop. */
    @Volatile
    private var client: MissClient? = null

    @Volatile
    private var schedule = AlarmSchedule(windowed = false)

    /** ALRM-16: learned steps for the current camera, applied on top of the sensitivity dial. */
    @Volatile
    private var calibrationSteps = 0

    // Room-level telemetry (LIVE-6): only touched from the audio window callback.
    private var levelStats = ArrayList<Double>(2048)
    private var levelStatsStartMs = 0L

    /** Keeps a slept-through alarm from writing a log line every second all night (WATCH-6). */
    @Volatile
    private var suppressionLogged = false

    /** ALRM-15: the camera that was ringing, captured at ring time (a switch may follow). */
    @Volatile
    private var alarmDid: String? = null

    fun start() {
        if (job?.isActive == true) {
            if (MonitorHub.status.value != STATUS_MONITOR_FAILED) {
                Log.i("engine", "start ignored — already monitoring")
                return
            }
            // WATCH-11/APP-3: an aux loop (watchdog, settings) died while the connection loop
            // lived on. "Press Resume to restart" must actually restart — a monitor without its
            // watchdog is broken even if audio still flows, so tear the survivor down too
            // (the new loop below waits for its teardown before touching the shared fields).
            Log.w("engine", "restarting after a monitor failure — cancelling the surviving loops")
        }
        Log.i("engine", "start monitoring")
        // The connection loop can return on its own (an expired session), leaving the aux jobs
        // running while `job` is complete. Starting again must not stack a second watchdog tick
        // loop and a second settings collector on top of the live ones.
        auxJobs.forEach { it.cancel() }
        auxJobs.clear()

        MonitorHub.running.value = true
        MonitorHub.sessionExpired.value = false
        MonitorHub.lastAudioAtMs = 0
        MonitorHub.applySettings(store.loadSettings())
        MonitorHub.onAcknowledge = ::acknowledgeAlarm
        // ALRM-16/17: this camera's learned tuning, loaded with clamped trust in what's stored.
        calibrationSteps = store.loadDevice()?.did
            ?.let { store.cryCalibrationSteps(it).coerceIn(0, CALIBRATION_MAX_STEPS) } ?: 0
        MonitorHub.calibrationSteps.value = calibrationSteps
        MonitorHub.onCryFeedback = ::applyCryFeedback
        MonitorHub.onCalibrationReset = ::resetCalibration
        store.setMonitoring(true) // BG-10: so a reboot can be reported

        // WATCH-11: any piece of the monitor dying abnormally — the connection loop OR the
        // watchdog tick loop — must be announced, never shown as "retrying". The service's
        // crashGuard keeps the process alive; this makes sure the failure is *said*.
        fun Job.announceIfItDies(what: String): Job = also { j ->
            j.invokeOnCompletion { cause ->
                if (cause != null && cause !is CancellationException) {
                    Log.e("engine", "$what died — monitoring is no longer working: ${cause.message}", cause)
                    MonitorHub.status.value = STATUS_MONITOR_FAILED
                }
            }
        }

        auxJobs += scope.launch {
            MonitorHub.settings.collect { s ->
                // A throw here would kill the collector for the life of the engine: the user would
                // turn the alarm on, see it on, and have nothing arm. Never let that happen.
                runCatching {
                    player?.muted = s.muted
                    detector.enabled = s.alarmEnabled
                    // ALRM-2/16: the dial plus this camera's learned steps set the loudness bar.
                    detector.thresholdDb = effectiveThresholdDb(s.alarmSensitivity, calibrationSteps)
                    schedule = AlarmSchedule.from(s)
                    // watchdog.enabled is set by the tick loop: arming depends on the wall clock.
                    watchdog.graceMs = s.watchdogGraceSeconds * 1000L
                }.onFailure { Log.w("engine", "could not apply settings: ${it.message}", it) }
            }
        }.announceIfItDies("settings collector")

        // WATCH-2/7: watch the feed itself, whatever the cause of silence.
        auxJobs += scope.launch {
            while (isActive) {
                delay(1000)
                if (!MonitorHub.running.value) {
                    watchdog.reset()
                    continue
                }
                val now = nowMs()
                val status = MonitorHub.status.value
                val lastAudio = MonitorHub.lastAudioAtMs

                // WATCH-7: a "live" connection that has gone quiet never recovers by itself —
                // the read just blocks. Force it closed so the normal reconnect path takes over.
                if (feedStalled(status, lastAudio, now)) {
                    Log.w("engine", "feed stalled (no audio for ${now - lastAudio}ms) — dropping the connection")
                    MonitorHub.status.value = "error: the camera stopped sending audio"
                    runCatching { client?.close() }
                }

                // WATCH-9: armed only while the crying alarm could itself ring (wall clock,
                // like ALRM-7 — "only at night" is a statement about the wall clock).
                val s = MonitorHub.settings.value
                watchdog.enabled =
                    StreamWatchdog.armed(s.watchdogEnabled, s.alarmEnabled, schedule, wallClockMinutesOfDay())

                if (watchdog.onTick(feedAlive(status, lastAudio, now), now)) {
                    // WATCH-6: if another alarm is sounding, take the fire back and retry later
                    // rather than lose the feed-down alarm for this outage entirely.
                    if (ringer.ring(AlarmKind.FEED_DOWN, MonitorHub.cameraName.value)) {
                        Log.w("engine", "watchdog: feed down past grace period — alarm")
                        suppressionLogged = false
                    } else {
                        watchdog.unfire() // retried every tick until it can actually be heard
                        if (!suppressionLogged) {
                            suppressionLogged = true
                            Log.w("engine", "watchdog alarm suppressed (another alarm ringing) — retrying")
                        }
                    }
                }
            }
        }.announceIfItDies("watchdog tick loop")

        val previous = job
        job = scope.launch {
            // Never overlap two connection loops on one engine: the old loop's finally writes
            // the shared client/player/renderer fields, and racing it could null or release the
            // new connection's objects. Its teardown is bounded (~1 s), so waiting is cheap.
            previous?.cancelAndJoin()
            connectionLoop()
        }.announceIfItDies("connection loop")
    }

    fun stop() {
        Log.i("engine", "stop monitoring")
        MonitorHub.running.value = false
        MonitorHub.status.value = STATUS_STOPPED
        // A stop closes the expiry story too: a stale flag left set here would sign the user
        // out again the next time the viewer opens (see connectOnce's signed-out path).
        MonitorHub.sessionExpired.value = false
        MonitorHub.level.value = 0f
        // The BG-10 reboot marker is NOT cleared here: stop() also runs on system-initiated
        // teardown (service onDestroy) and camera switches. Only the user-intent paths — the
        // Stop action and signing out — clear it.
        ringer.acknowledge() // stopping monitoring silences any ringing alarm
        watchdog.reset()
        auxJobs.forEach { it.cancel() }
        auxJobs.clear()
        job?.cancel()
        job = null
    }

    private fun acknowledgeAlarm() {
        // Read what is ringing before acknowledge clears it — the answer belongs to THIS alarm.
        val acknowledged = MonitorHub.activeAlarm.value
        ringer.acknowledge()
        detector.suppressed = false
        if (acknowledged == AlarmKind.BABY_NOISE) {
            // ALRM-5: the cooldown is the crying alarm's own. Acknowledging a feed-drop alarm
            // must not snooze cry detection — the feed may recover with the baby crying.
            detector.snooze(nowMs() + ALARM_COOLDOWN_MS)
        }
        // The watchdog stays quiet for this outage by itself; recovery re-arms it (WATCH-3).

        // ALRM-15: a crying alarm earns the question; the answer is pinned to the camera that
        // alarmed, so it still lands right if the user switches cameras before answering.
        if (acknowledged != null && asksForCryFeedback(acknowledged)) {
            (alarmDid ?: store.loadDevice()?.did)?.let { MonitorHub.pendingCryFeedback.value = it }
        }
        alarmDid = null
    }

    /** ALRM-16: one yes/no from the parent moves this camera's learned tuning one step. */
    private fun applyCryFeedback(did: String, wasCry: Boolean) {
        val stored = store.cryCalibrationSteps(did).coerceIn(0, CALIBRATION_MAX_STEPS)
        val steps = if (wasCry) afterRealCry(stored) else afterFalseAlarm(stored)
        store.saveCryCalibrationSteps(did, steps) // ALRM-17: persists
        Log.i(
            "engine",
            "cry feedback for $did: ${if (wasCry) "real cry" else "false alarm"} → " +
                "$steps step(s) above the dial",
        )
        applyCalibrationIfCurrent(did, steps)
    }

    /** ALRM-17: forget the current camera's learned tuning — back to the dial alone. */
    private fun resetCalibration() {
        val did = store.loadDevice()?.did ?: return
        store.saveCryCalibrationSteps(did, 0)
        Log.i("engine", "cry calibration reset for $did")
        applyCalibrationIfCurrent(did, 0)
    }

    private fun applyCalibrationIfCurrent(did: String, steps: Int) {
        if (did != store.loadDevice()?.did) return // feedback for a camera we've moved away from
        calibrationSteps = steps
        MonitorHub.calibrationSteps.value = steps
        // Effective immediately (ALRM-16), like every other alarm setting (ALRM-2).
        detector.thresholdDb = effectiveThresholdDb(MonitorHub.settings.value.alarmSensitivity, steps)
    }

    private suspend fun connectionLoop() {
        var attempt = 0
        while (scope.isActive && MonitorHub.running.value) {
            try {
                connectOnce()
                attempt = 0 // a successful session resets the backoff (LIVE-5)
            } catch (e: CancellationException) {
                throw e
            } catch (e: AuthExpiredException) {
                // BG-8: retrying is pointless — say so and stop. Monitoring stays "running" so the
                // watchdog still alarms: an expired session at 3am means the baby is unmonitored.
                Log.w("engine", "session expired — stopping reconnects until the user signs in", e)
                MonitorHub.status.value = STATUS_SESSION_EXPIRED
                MonitorHub.sessionExpired.value = true
                job = null // this loop is done; a later start() must be able to begin a fresh one
                return
            } catch (e: UnsupportedCameraException) {
                // LIVE-12: retrying can never fix this — say so instead of looping "connection
                // lost" forever. Monitoring stays "running" so the status is shown and the
                // watchdog still guards armed hours (WATCH-2).
                Log.w("engine", "unsupported camera — stopping reconnects: ${e.message}")
                MonitorHub.status.value = STATUS_UNSUPPORTED_CAMERA
                job = null
                return
            } catch (e: Exception) {
                Log.w("engine", "connection ended: ${e.message}", e)
                MonitorHub.status.value = "error: ${e.message ?: e.toString()}"
            }
            if (!MonitorHub.running.value) return
            val wait = reconnectDelayMs(attempt)
            attempt++
            Log.i("engine", "reconnecting in ${wait}ms (attempt $attempt)")
            // LIVE-4: a live countdown, ticked once a second, not a frozen message.
            var remaining = wait
            while (remaining > 0 && MonitorHub.running.value) {
                MonitorHub.status.value = reconnectStatus(remaining)
                val step = minOf(1000L, remaining)
                delay(step)
                remaining -= step
            }
        }
    }

    private suspend fun connectOnce() {
        val session = store.loadSession() ?: run {
            // An empty store means the user signed out mid-flight (AUTH-10) — a deliberate stop,
            // never an expiry. Declaring it expired would poison the expired flag and bounce the
            // *next* sign-in straight back out.
            Log.i("engine", "no stored session — signed out; stopping monitoring")
            store.setMonitoring(false) // BG-10: signing out is a deliberate stop
            MonitorHub.running.value = false
            MonitorHub.status.value = STATUS_STOPPED
            return
        }
        var device = store.loadDevice() ?: throw XiaomiException("no camera selected")
        Log.i("engine", "connecting to ${device.name} did=${device.did} model=${device.model} ip=${device.ip}")
        MonitorHub.cameraName.value = device.name
        MonitorHub.status.value = STATUS_CONNECTING

        val cloud = MiCloud(platformHttp, session = session)
        cloud.onSessionRefreshed = { store.saveSession(it) } // AUTH-7

        // Refresh the camera's LAN address in case DHCP moved it since last time.
        try {
            cloud.deviceList().firstOrNull { it.did == device.did }?.let {
                if (it.ip.isNotEmpty() && it != device) {
                    Log.i("engine", "camera address updated: ${device.ip} -> ${it.ip}")
                    device = it
                    store.saveDevice(it)
                }
            }
        } catch (e: AuthExpiredException) {
            throw e // BG-8: a dead session is not a "using the stored ip" situation
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w("engine", "device-list refresh failed (using stored ip ${device.ip}): ${e.message}")
        }

        if (device.ip.isEmpty()) {
            Log.w("engine", "camera has no LAN ip — is it online and on the same network?")
        }

        val (pub, priv) = Crypto.generateBoxKeyPair()
        val vendor = cloud.missGetVendor(device.did, pub)
        if (vendorName(vendor.vendor) != "cs2") {
            throw UnsupportedCameraException("this camera speaks '${vendorName(vendor.vendor)}' — only cs2 is supported")
        }

        val missClient = MissClient(device.model, platformSockets)
        client = missClient
        // Everything from here on must be released on EVERY exit path. A CS2 connection owns its
        // own socket and a 1 Hz keepalive coroutine that only close() ever stops — leaking one per
        // failed handshake would, over a night of retries, exhaust the phone's file descriptors
        // and kill the monitor silently.
        try {
            // WATCH-7: a camera that accepts the socket but never finishes the handshake must not
            // leave us stuck on "Connecting…" forever.
            try {
                withTimeout(CONNECT_TIMEOUT_MS) {
                    missClient.connect(
                        MissClient.ConnectParams(
                            ip = device.ip,
                            vendor = "cs2",
                            transport = "tcp",
                            clientPublic = pub,
                            clientPrivate = priv,
                            devicePublicHex = vendor.devicePublicHex,
                            sign = vendor.sign,
                        ),
                    )
                    missClient.startMedia(quality = "hd", audio = true)
                }
            } catch (e: TimeoutCancellationException) {
                throw XiaomiException("the camera did not answer in time")
            }

            val transport = if (missClient.conn.isTcp) "cs2+tcp" else "cs2+udp"
            Log.i("engine", "media requested from ${device.name} (${device.ip}) via $transport")
            // WATCH-7: the session being up is not the feed being live. A camera that is switched
            // off still completes this handshake; only audio proves the feed. Stay "connecting".
            MonitorHub.lastAudioAtMs = 0
            MonitorHub.status.value = STATUS_CONNECTING

            meter = LevelMeter() // fresh baseline per connection
            val audioPlayer = media.audio(onPcmWindow = ::onPcmWindow)
            // Publish before configuring or starting: (a) a mute toggle landing between snapshot
            // and publish would hit player == null and be lost for the whole connection; (b) if
            // start() throws, the finally must still release the half-built codec. Leaking one
            // per retry would exhaust the device's decoders — and a device with no decoder left
            // has no audio, no level meter and no noise alarm.
            player = audioPlayer
            audioPlayer.muted = MonitorHub.settings.value.muted
            audioPlayer.start()
            // VideoOutput.push never throws — video trouble never takes audio down (LIVE-7).
            val videoRenderer = media.video()
            renderer = videoRenderer

            // LIVE-8: the reader never blocks on decoding/playback, and audio and video are
            // consumed independently, each dropping backlog rather than accumulating delay.
            val audioQueue = Channel<Frame.Audio>(
                capacity = AUDIO_MAX_BACKLOG_PACKETS,
                onBufferOverflow = BufferOverflow.DROP_OLDEST,
            )
            val videoQueue = Channel<Frame.Video>(Channel.UNLIMITED)
            val videoBacklog = Counter()
            // LIVE-12: audio frames seen in a codec we cannot play, while no opus has arrived.
            val unsupportedAudio = Counter()

            coroutineScope {
                launch {
                    for (f in audioQueue) audioPlayer.push(f.data, f.pts)
                }
                launch {
                    val catchup = VideoCatchup()
                    for (f in videoQueue) {
                        val backlog = videoBacklog.decrement()
                        if (catchup.admit(Hevc.isKeyframe(f.data), backlog)) {
                            videoRenderer.push(f.data, f.pts)
                        }
                    }
                }
                // WATCH-7: if no audio ever arrives, the read below would block forever. Give the
                // camera a bounded chance to send its first audio, then drop and reconnect.
                val firstFrameGuard = launch {
                    delay(FIRST_FRAME_TIMEOUT_MS)
                    if (MonitorHub.lastAudioAtMs == 0L) {
                        // LIVE-12: any unsupported audio and no opus by the deadline is the same
                        // verdict as the frame-count give-up — a slow camera must not evade it
                        // into the reconnect loop.
                        if (unsupportedAudio.get() > 0) {
                            throw UnsupportedCameraException("the camera's audio format isn't supported")
                        }

                        Log.w("engine", "no audio within ${FIRST_FRAME_TIMEOUT_MS}ms — dropping the connection")
                        MonitorHub.status.value = "error: the camera sent no audio"
                        runCatching { missClient.close() }
                    }
                }

                try {
                    var sawVideo = false
                    var unsupportedVideoLogged = false
                    while (isActive && MonitorHub.running.value) {
                        val frame = missClient.readFrame()
                        when (frame) {
                            is Frame.Audio -> if (frame.codec == "opus") {
                                if (MonitorHub.lastAudioAtMs == 0L) {
                                    // LIVE-4 / WATCH-7: only audio proves the feed. Video may
                                    // already be rendering — it never flips us live by itself.
                                    Log.i("engine", "LIVE: ${device.name} (${device.ip}) via $transport (opus ${frame.sampleRate}Hz)")
                                    MonitorHub.status.value = STATUS_LIVE
                                }
                                MonitorHub.lastAudioAtMs = nowMs()
                                audioQueue.send(frame)
                            } else if (MonitorHub.lastAudioAtMs == 0L) {
                                // LIVE-12: audio we cannot decode can never be monitored — a
                                // camera sending only such audio is permanent, not a blip.
                                val seen = unsupportedAudio.increment()
                                if (seen == 1) {
                                    Log.w("engine", "unsupported audio codec ${frame.codec} — cannot monitor with it")
                                }
                                if (seen >= UNSUPPORTED_AUDIO_GIVE_UP) {
                                    throw UnsupportedCameraException(
                                        "the camera sends ${frame.codec} audio, which isn't supported",
                                    )
                                }
                            }
                            is Frame.Video -> if (frame.codec == "h265") {
                                if (!sawVideo) {
                                    sawVideo = true
                                    Log.i("engine", "first video frame (h265)")
                                }
                                videoBacklog.increment()
                                videoQueue.trySend(frame)
                            } else if (!unsupportedVideoLogged) {
                                // A camera sending only e.g. h264 shows a black picture; never
                                // let that be a silent mystery. Audio monitoring is unaffected
                                // (LIVE-7), so this is a log, not a failure.
                                unsupportedVideoLogged = true
                                Log.w("engine", "unsupported video codec ${frame.codec} — no picture; audio monitoring continues")
                            }
                        }
                    }
                } finally {
                    firstFrameGuard.cancel() // never let it hold up teardown
                    // Closing the queues lets both consumers drain and finish.
                    audioQueue.close()
                    videoQueue.close()
                }
            }
        } finally {
            // Runs on every path out — handshake failure, decoder failure, stall, cancellation.
            // On cancellation (user stop, camera switch) a plain suspending call would throw
            // before sending anything — give stopMedia a bounded, non-cancellable window so the
            // camera actually hears it.
            runCatching {
                withContext(NonCancellable) { withTimeoutOrNull(1_000) { missClient.stopMedia() } }
            }
            runCatching { missClient.close() }
            client = null
            runCatching { player?.release() }
            runCatching { renderer?.release() }
            player = null
            renderer = null
        }
    }

    /**
     * One compact room-level line every 30 s, so a night can be reconstructed from logcat:
     * was the room quiet, what stood out, where the ambient floor sat. A quiet room should
     * read "median 0.0" — anything else is either a real event or a metering bug (LIVE-6).
     */
    private fun logRoomLevel(levelDb: Double, nowMs: Long) {
        if (levelStatsStartMs == 0L) levelStatsStartMs = nowMs
        levelStats.add(levelDb)
        if (nowMs - levelStatsStartMs < 30_000) return
        val sorted = levelStats.sorted()
        Log.d(
            "engine",
            "room level last ${(nowMs - levelStatsStartMs) / 1000}s: " +
                "median ${oneDecimal(sorted[sorted.size / 2])} max ${oneDecimal(sorted.last())} " +
                "dB above ambient (floor ${oneDecimal(meter.floorDb)} dBFS, ${sorted.size} windows)",
        )
        levelStats = ArrayList(2048)
        levelStatsStartMs = nowMs
    }

    private fun onPcmWindow(pcm: ShortArray, sampleRate: Int) {
        val now = nowMs()
        val metrics = analyzeWindow(pcm, sampleRate)
        val level = meter.process(metrics.rms, metrics.peak, now)
        MonitorHub.level.value = level.toFloat()
        logRoomLevel(level, now)
        val windowMs = pcm.size * 1000L / sampleRate
        if (detector.onWindow(level, metrics, windowMs, now)) {
            // ALRM-7: only ring inside the armed window (wall clock, deliberately).
            if (schedule.isActive(wallClockMinutesOfDay())) {
                // ALRM-5 / WATCH-6: only suppress further triggers if the alarm actually started.
                if (ringer.ring(AlarmKind.BABY_NOISE, MonitorHub.cameraName.value)) { // ALRM-4
                    detector.suppressed = true // nothing new until acknowledged
                    alarmDid = store.loadDevice()?.did // ALRM-15: pin the camera that alarmed
                }
            }
        }
    }
}
