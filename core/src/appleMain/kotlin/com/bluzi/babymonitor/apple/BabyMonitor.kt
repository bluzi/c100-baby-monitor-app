package com.bluzi.babymonitor.apple

import com.bluzi.babymonitor.data.AppStore
import com.bluzi.babymonitor.data.KeyValueStore
import com.bluzi.babymonitor.data.SecretBox
import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.monitor.AppleMedia
import com.bluzi.babymonitor.monitor.AppleRinger
import com.bluzi.babymonitor.monitor.CameraControl
import com.bluzi.babymonitor.monitor.LevelMeter
import com.bluzi.babymonitor.monitor.MonitorEngine
import com.bluzi.babymonitor.monitor.MonitorHub
import com.bluzi.babymonitor.monitor.STATUS_IDLE
import com.bluzi.babymonitor.monitor.STATUS_STOPPED
import com.bluzi.babymonitor.monitor.alarmSoundDescription
import com.bluzi.babymonitor.monitor.alarmSoundLabel
import com.bluzi.babymonitor.monitor.displayLevelDb
import com.bluzi.babymonitor.monitor.effectiveThresholdDb
import com.bluzi.babymonitor.monitor.friendlyStatus
import com.bluzi.babymonitor.monitor.statusLine
import com.bluzi.babymonitor.net.platformHttp
import com.bluzi.babymonitor.net.toNSData
import com.bluzi.babymonitor.platform.wallClockMs
import com.bluzi.babymonitor.ui.Screen
import com.bluzi.babymonitor.ui.ViewerActionKind
import com.bluzi.babymonitor.ui.route
import com.bluzi.babymonitor.ui.viewerActionKinds
import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.LoginResult
import com.bluzi.babymonitor.xiaomi.MiCloud
import com.bluzi.babymonitor.xiaomi.NightVisionMode
import com.bluzi.babymonitor.xiaomi.REGIONS
import com.bluzi.babymonitor.xiaomi.isCamera
import kotlin.concurrent.Volatile
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import platform.Foundation.NSData

/**
 * The whole monitor, as one flat object Swift can call.
 *
 * The macOS app is a shell. It draws a menu bar item, some windows and a picture, and it calls
 * this. It never sees a coroutine, a Flow, or a Kotlin sealed class — those cross the Swift bridge
 * badly, and a bridge that is unpleasant is a bridge people work around. Everything here is plain
 * values and callbacks, and every callback lands on the main queue.
 *
 * What is deliberately NOT here: any decision. Whether monitoring can be stopped, when the alarm
 * rings, what the status line says, how loud is loud — all of that is the shared monitor's, and is
 * the same code the phone runs. The shell asks; it does not decide.
 */
@OptIn(ExperimentalForeignApi::class)
object BabyMonitor {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val engineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private lateinit var store: AppStore
    private lateinit var ringer: AppleRinger
    private var engine: MonitorEngine? = null

    private var listener: ((UiState) -> Unit)? = null

    /** The sign-in step waiting on the user (a captcha image, or a code we asked Xiaomi to send). */
    @Volatile
    private var pendingLogin: LoginResult? = null

    /** BG-12 / DESK-21: when the Mac went to sleep, and how long the monitor was therefore dead. */
    @Volatile
    private var sleptAtMs: Long = 0

    @Volatile
    private var sleepOutage: String? = null

    /**
     * What [state] needs to route (APP-1), cached.
     *
     * Reading it from the store instead would mean touching the secret store on every UI update —
     * and the UI updates on every level-meter tick, about twenty times a second. On macOS that is
     * the Keychain, so the app hammered it at 20 Hz and, whenever a read was not already
     * authorised, raised an endless queue of password prompts.
     *
     * These change only when the user signs in, signs out, or picks a camera. That is when they
     * are refreshed, and never in the hot path.
     */
    @Volatile
    private var hasSession = false

    @Volatile
    private var hasDevice = false

    private fun refreshRouting() {
        hasSession = store.loadSession() != null
        hasDevice = store.loadDevice() != null
    }

    // --- lifecycle -----------------------------------------------------------

    /**
     * Wire the shell's storage in and start watching state. [logSink] receives (level, tag,
     * message) so the app can route it to os_log.
     */
    fun install(
        keyValueStore: KeyValueStore,
        secretBox: SecretBox,
        logSink: (level: String, tag: String, message: String) -> Unit,
    ) {
        Log.install { level, tag, message, error ->
            val text = if (error != null) "$message — ${error.message}" else message
            logSink(level.name, tag, text)
        }
        store = AppStore(keyValueStore, secretBox)
        ringer = AppleRinger(engineScope)
        MonitorHub.applySettings(store.loadSettings())
        refreshRouting()

        // One listener over everything the UI can show, so the shell never subscribes to a Flow.
        scope.launch {
            combine(
                MonitorHub.running,
                MonitorHub.status,
                MonitorHub.level,
                MonitorHub.cameraName,
                MonitorHub.settings,
            ) { _, _, _, _, _ -> Unit }.collect { emit() }
        }
        scope.launch {
            combine(
                MonitorHub.activeAlarm,
                MonitorHub.sessionExpired,
                MonitorHub.pendingCryFeedback,
                MonitorHub.calibrationSteps,
            ) { _, _, _, _ -> Unit }.collect { emit() }
        }
    }

    fun onStateChange(listener: (UiState) -> Unit) {
        this.listener = listener
        emit()
    }

    private fun emit() {
        listener?.invoke(state())
    }

    fun state(): UiState {
        val settings = MonitorHub.settings.value
        val status = MonitorHub.status.value
        val running = MonitorHub.running.value
        val screen = route(hasSession, hasDevice) // cached — see refreshRouting()
        // DESK-11: is the monitor doing exactly what the parent thinks it is? Decided once, here,
        // so the window and the tile cannot come to different answers.
        val health = MonitorHealth(
            running = running,
            status = status,
            activeAlarm = MonitorHub.activeAlarm.value?.name,
            sessionExpired = MonitorHub.sessionExpired.value,
            sleepOutage = sleepOutage,
        )
        return UiState(
            health = health,
            needsAttention = MacShell.needsAttention(health),
            screen = when (screen) {
                Screen.Login -> "login"
                Screen.Devices -> "devices"
                Screen.Viewer -> "viewer"
            },
            running = running,
            status = status,
            statusText = friendlyStatus(status),
            statusLine = statusLine(MonitorHub.cameraName.value, status, settings.muted),
            cameraName = MonitorHub.cameraName.value,
            level = displayLevelDb(MonitorHub.level.value.toDouble()).toFloat(),
            levelMax = LevelMeter.LEVEL_MAX.toFloat(),
            thresholdDb = effectiveThresholdDb(
                settings.alarmSensitivity,
                MonitorHub.calibrationSteps.value,
            ).toFloat(),
            muted = settings.muted,
            alarmEnabled = settings.alarmEnabled,
            activeAlarm = MonitorHub.activeAlarm.value?.name,
            sessionExpired = MonitorHub.sessionExpired.value,
            askingCryFeedback = MonitorHub.pendingCryFeedback.value != null,
            calibrationSteps = MonitorHub.calibrationSteps.value,
            sleepOutage = sleepOutage,
            // BG-11 / WATCH-11: the shared decision. The Mac's menu and the phone's button row
            // cannot come to different conclusions about what is offered.
            canStop = ViewerActionKind.Stop in viewerActionKinds(running, status),
            canResume = ViewerActionKind.Resume in viewerActionKinds(running, status),
        )
    }

    // --- sign-in -------------------------------------------------------------

    fun regions(): List<String> = REGIONS

    fun signIn(username: String, password: String, region: String, done: (SignInResult) -> Unit) {
        scope.launch {
            val cloud = MiCloud(platformHttp, region = region)
            finishSignIn(done) { cloud.login(username, password) }
        }
    }

    fun submitCaptcha(code: String, done: (SignInResult) -> Unit) {
        val pending = pendingLogin as? LoginResult.Captcha
            ?: return done(SignInResult("error", message = "There is no captcha to answer."))
        scope.launch { finishSignIn(done) { pending.submit(code) } }
    }

    fun submitCode(ticket: String, done: (SignInResult) -> Unit) {
        val pending = pendingLogin as? LoginResult.TwoFactor
            ?: return done(SignInResult("error", message = "There is no code to submit."))
        scope.launch { finishSignIn(done) { pending.submit(ticket) } }
    }

    private suspend fun finishSignIn(done: (SignInResult) -> Unit, step: suspend () -> LoginResult) {
        val result = try {
            step()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            // AUTH-9: a failed sign-in reads as words, never as the gateway's raw JSON.
            done(SignInResult("error", message = e.message ?: "Sign-in failed."))
            return
        }
        pendingLogin = result
        when (result) {
            is LoginResult.Ok -> {
                store.saveSession(result.session)
                pendingLogin = null
                refreshRouting()
                done(SignInResult("ok"))
                emit()
            }
            is LoginResult.Captcha -> done(
                SignInResult("captcha", captchaImage = result.image.toNSData()),
            )
            is LoginResult.TwoFactor -> done(
                SignInResult("code", channel = result.channel, maskedTarget = result.maskedTarget),
            )
        }
    }

    /** AUTH-10: signing out forgets the session AND the selected camera. */
    fun signOut() {
        stop()
        store.signOut()
        store.setMonitoring(false)
        pendingLogin = null
        MonitorHub.sessionExpired.value = false
        refreshRouting()
        emit()
    }

    // --- cameras -------------------------------------------------------------

    fun loadCameras(done: (List<CameraInfo>?, String?) -> Unit) {
        scope.launch {
            try {
                val session = store.loadSession()
                if (session == null) {
                    done(null, "You are not signed in.")
                    return@launch
                }
                val cloud = MiCloud(platformHttp, session = session)
                cloud.onSessionRefreshed = { store.saveSession(it) } // AUTH-7
                val cameras = cloud.deviceList()
                    .filter { isCamera(it.model) } // PROTO-11
                    .map { CameraInfo(it.did, it.name, it.model, it.mac, it.ip) }
                done(cameras, null)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                // CAM-5 / APP-3: a readable message with a way to retry — never a dead end.
                done(null, e.message ?: "Could not load your cameras.")
            }
        }
    }

    fun selectCamera(camera: CameraInfo) {
        store.saveDevice(Device(camera.did, camera.name, camera.model, camera.mac, camera.ip))
        refreshRouting()
        emit()
    }

    fun selectedCamera(): CameraInfo? = store.loadDevice()?.let {
        CameraInfo(it.did, it.name, it.model, it.mac, it.ip)
    }

    /** CAM-4: switching camera stops the old stream and sends the user back to the picker. */
    fun switchCamera() {
        stop()
        store.clearDevice()
        refreshRouting()
        emit()
    }

    // --- monitoring ----------------------------------------------------------

    fun start() {
        val e = engine ?: MonitorEngine(store, engineScope, ringer, AppleMedia).also { engine = it }
        e.start()
        emit()
    }

    fun stop() {
        engine?.stop()
        store.setMonitoring(false)
        emit()
    }

    /**
     * BG-10i: was monitoring running when the process last ended? An iPhone cannot relaunch or notify
     * after a reboot or a force-quit, so on relaunch the shell asks this to report the outage honestly
     * rather than come back as though the watch had never stopped. (The engine sets the flag on start
     * and clears it on a deliberate stop/sign-out — MonitorEngine.)
     */
    fun wasMonitoring(): Boolean = store.wasMonitoring()

    fun acknowledge() {
        MonitorHub.acknowledge()
        emit()
    }

    fun submitCryFeedback(wasCry: Boolean) {
        MonitorHub.submitCryFeedback(wasCry)
        emit()
    }

    fun dismissCryFeedback() {
        MonitorHub.dismissCryFeedback()
        emit()
    }

    fun resetCalibration() {
        MonitorHub.resetCalibration()
        emit()
    }

    // --- settings ------------------------------------------------------------

    /** Settings cross the bridge as JSON — one shape, already tested, no second model to drift. */
    fun settingsJson(): String = MonitorHub.settings.value.toJson()

    fun saveSettingsJson(json: String) {
        val settings = Settings.fromJson(json)
        store.saveSettings(settings)
        MonitorHub.applySettings(settings) // ALRM-2: effective immediately
        emit()
    }

    fun setMuted(muted: Boolean) {
        saveSettingsJson(MonitorHub.settings.value.copy(muted = muted).toJson())
    }

    fun alarmSounds(): List<AlarmSoundInfo> = Settings.ALARM_SOUNDS.map {
        AlarmSoundInfo(it, alarmSoundLabel(it), alarmSoundDescription(it))
    }

    fun previewAlarm(sound: String, volume: Double) {
        ringer.preview(sound, volume)
    }

    // --- camera controls -----------------------------------------------------

    fun nightVision(done: (String?, String?) -> Unit) {
        scope.launch {
            try {
                done(CameraControl.getNightVision(store)?.name, null)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                done(null, e.message ?: "Could not read the camera's night vision setting.")
            }
        }
    }

    fun setNightVision(mode: String, done: (String?) -> Unit) {
        scope.launch {
            try {
                val parsed = NightVisionMode.entries.firstOrNull { it.name == mode }
                    ?: throw IllegalArgumentException("unknown night-vision mode '$mode'")
                CameraControl.setNightVision(store, parsed)
                done(null)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                // LIVE-10: a readable error, and the shown mode is left as it was.
                done(e.message ?: "Could not change the camera's night vision setting.")
            }
        }
    }

    // --- sleep and wake (BG-12 / DESK-21) -----------------------------------

    /**
     * The Mac is about to sleep, and nothing we can do will stop it — this is the one thing a
     * phone can survive and a Mac cannot. Record when, so the outage can be reported honestly.
     */
    fun systemWillSleep() {
        if (!MonitorHub.running.value) return
        sleptAtMs = wallClockMs()
        Log.w("app", "the Mac is going to sleep — monitoring stops until it wakes")
    }

    /**
     * DESK-21. Three things must happen, and the middle one is the one that would otherwise be a
     * silent lie:
     *
     *  1. the outage is reported, with its duration — never a quiet reconnect;
     *  2. the feed is marked dead. The monotonic clock does NOT advance while a Mac sleeps, so the
     *     last audio frame still looks like it arrived moments ago. Left alone, the watchdog would
     *     conclude the feed had been alive all night. It was not.
     *  3. the connection is dropped so the normal reconnect path runs (LIVE-5).
     */
    fun systemDidWake() {
        val sleptAt = sleptAtMs
        sleptAtMs = 0
        if (sleptAt == 0L || !MonitorHub.running.value) return

        val outageMs = wallClockMs() - sleptAt
        val minutes = (outageMs / 60_000).coerceAtLeast(1)
        sleepOutage = "The Mac slept, so the monitor was down for about " +
            if (minutes < 60) "$minutes minute${if (minutes == 1L) "" else "s"}."
            else "${minutes / 60}h ${minutes % 60}m."
        Log.w("app", "the Mac woke after ${outageMs}ms asleep — monitoring was down for that whole time")

        MonitorHub.lastAudioAtMs = 0 // the feed is NOT live, whatever the monotonic clock thinks
        engine?.start() // the sockets died with the machine; reconnect
        emit()
    }

    fun clearSleepOutage() {
        sleepOutage = null
        emit()
    }
}

// --- values that cross the bridge -------------------------------------------

data class UiState(
    /** DESK-11: everything the shell needs to know before it may let the monitor fade. */
    val health: MonitorHealth,
    val needsAttention: Boolean,
    val screen: String, // "login" | "devices" | "viewer"
    val running: Boolean,
    val status: String,
    val statusText: String,
    val statusLine: String,
    val cameraName: String,
    val level: Float,
    val levelMax: Float,
    val thresholdDb: Float,
    val muted: Boolean,
    val alarmEnabled: Boolean,
    val activeAlarm: String?, // "BABY_NOISE" | "FEED_DOWN" | null
    val sessionExpired: Boolean,
    val askingCryFeedback: Boolean,
    val calibrationSteps: Int,
    val sleepOutage: String?,
    val canStop: Boolean,
    val canResume: Boolean,
)

data class CameraInfo(
    val did: String,
    val name: String,
    val model: String,
    val mac: String,
    val ip: String,
)

data class AlarmSoundInfo(val id: String, val label: String, val description: String)

@OptIn(ExperimentalForeignApi::class)
class SignInResult(
    /** "ok" | "captcha" | "code" | "error" */
    val kind: String,
    val message: String? = null,
    val captchaImage: NSData? = null,
    val channel: String? = null,
    val maskedTarget: String? = null,
)
