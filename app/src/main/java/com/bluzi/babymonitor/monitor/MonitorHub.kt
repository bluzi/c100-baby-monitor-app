package com.bluzi.babymonitor.monitor

import android.view.Surface
import com.bluzi.babymonitor.data.Settings
import kotlinx.coroutines.flow.MutableStateFlow

/**
 * Shared state between the foreground service (the monitor itself — BG-5) and whatever UI
 * happens to be attached. The UI is a thin observer; the service is the owner.
 */
object MonitorHub {
    val running = MutableStateFlow(false)
    val status = MutableStateFlow("idle")
    val level = MutableStateFlow(0f) // dB above ambient floor, 0..LevelMeter.LEVEL_MAX (LIVE-6)
    val cameraName = MutableStateFlow("")

    /** Settings mirror (LIVE-2/3, ALRM-2/7, WATCH-1): UI persists, then updates this. */
    val settings = MutableStateFlow(Settings())

    /** The currently-ringing, unacknowledged alarm (ALRM-4 / WATCH-3), if any. */
    val activeAlarm = MutableStateFlow<AlarmKind?>(null)

    /**
     * ALRM-15: the camera (did) whose acknowledged crying alarm still awaits the "was the baby
     * crying?" answer — null when there is nothing to ask. Only ever the most recent alarm.
     */
    val pendingCryFeedback = MutableStateFlow<String?>(null)

    /** ALRM-16/17: learned steps for the current camera, mirrored by the engine for the UI. */
    val calibrationSteps = MutableStateFlow(0)

    /** BG-8: the session died and no retry can fix it — the UI must send the user to sign-in. */
    val sessionExpired = MutableStateFlow(false)

    // Video output surface — null while no UI is showing; audio never depends on it (LIVE-7).
    val surface = MutableStateFlow<Surface?>(null)

    /**
     * Monotonic timestamp (elapsed realtime, not wall clock) of the last *decodable audio* frame —
     * feeds the watchdog's liveness check (WATCH-2). Audio is what monitoring means: video alone,
     * or audio in a codec we cannot play, is not a live feed (WATCH-7). Wall clock would let an
     * overnight NTP/DST correction hide an outage, so it must never be used here.
     */
    @Volatile
    var lastAudioAtMs: Long = 0L

    /** Set by the engine; routes acknowledge presses (app button or notification action). */
    @Volatile
    var onAcknowledge: (() -> Unit)? = null

    /** Set by the engine; applies a "was the baby crying?" answer to a camera (ALRM-16). */
    @Volatile
    var onCryFeedback: ((did: String, wasCry: Boolean) -> Unit)? = null

    /** Set by the engine; forgets the current camera's learned tuning (ALRM-17). */
    @Volatile
    var onCalibrationReset: (() -> Unit)? = null

    fun applySettings(s: Settings) {
        settings.value = s
    }

    fun acknowledge() {
        onAcknowledge?.invoke()
    }

    /** ALRM-15/16: answer the pending question. A yes/no with no question pending does nothing. */
    fun submitCryFeedback(wasCry: Boolean) {
        val did = pendingCryFeedback.value ?: return
        pendingCryFeedback.value = null
        onCryFeedback?.invoke(did, wasCry)
    }

    /** ALRM-15: the question is optional — dismissing it learns nothing and asks nothing again. */
    fun dismissCryFeedback() {
        pendingCryFeedback.value = null
    }

    fun resetCalibration() {
        onCalibrationReset?.invoke()
    }
}
