package com.bluzi.babymonitor.data

import com.bluzi.babymonitor.json.JSONObject
import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.Session
import com.bluzi.babymonitor.xiaomi.base64ToBytes
import com.bluzi.babymonitor.xiaomi.toBase64
import kotlin.concurrent.Volatile
import kotlin.math.roundToInt

// Persistence: session (encrypted — AUTH-5/6), selected camera (CAM-2/3), settings
// (LIVE-2, ALRM-6). Pure serialization + seams; Android impls live in AndroidStore.kt.

interface KeyValueStore {
    fun get(key: String): String?
    fun put(key: String, value: String)
    fun remove(key: String)
}

/**
 * Encryption seam for secrets — Android Keystore on the phone, the Keychain on the Mac,
 * passthrough fake in tests.
 *
 * **Failure is a null, never an exception.** Both are real: a Keystore key can be invalidated by an
 * OS update, and a Keychain can refuse. But an implementation on the other side of a language
 * bridge cannot throw something this side can catch — an ObjC exception raised in Swift unwinds
 * straight through Kotlin and kills the process. It did, once: a keychain that declined to store a
 * token took the whole monitor down with it. So the contract is a value, which cannot be misused.
 */
interface SecretBox {
    /** Null when the secret could not be sealed. The caller must then NOT persist it (AUTH-6). */
    fun seal(plain: String): String?

    /** Null when the secret could not be read back — a lost session, never a crash. */
    fun open(sealed: String): String?
}

object Codecs {
    fun sessionToJson(s: Session): String = JSONObject()
        .put("userId", s.userId)
        .put("cUserId", s.cUserId)
        .put("passToken", s.passToken)
        .put("serviceToken", s.serviceToken)
        .put("ssecurity", s.ssecurity.toBase64())
        .put("region", s.region)
        .toString()

    fun sessionFromJson(json: String): Session? = try {
        val v = JSONObject(json)
        Session(
            userId = v.getString("userId"),
            cUserId = v.optString("cUserId"),
            passToken = v.getString("passToken"),
            serviceToken = v.getString("serviceToken"),
            ssecurity = v.getString("ssecurity").base64ToBytes(),
            region = v.getString("region"),
        )
    } catch (_: Exception) {
        null
    }

    fun deviceToJson(d: Device): String = JSONObject()
        .put("did", d.did)
        .put("name", d.name)
        .put("model", d.model)
        .put("mac", d.mac)
        .put("ip", d.ip)
        .toString()

    fun deviceFromJson(json: String): Device? = try {
        val v = JSONObject(json)
        Device(
            did = v.getString("did"),
            name = v.optString("name"),
            model = v.optString("model"),
            mac = v.optString("mac"),
            ip = v.optString("ip"),
        )
    } catch (_: Exception) {
        null
    }
}

data class Settings(
    val muted: Boolean = false,
    val alarmEnabled: Boolean = false,
    // ALRM-2: the one detection control — 1 (needs loud crying) .. 10 (quiet crying), middle default.
    val alarmSensitivity: Int = SENSITIVITY_DEFAULT,
    // ALRM-7: "always" or a daily window (minutes since midnight, may cross midnight).
    val alarmScheduleMode: String = SCHEDULE_ALWAYS,
    val alarmWindowStartMinutes: Int = 19 * 60,
    val alarmWindowEndMinutes: Int = 7 * 60,
    // WATCH-1: feed watchdog.
    val watchdogEnabled: Boolean = false,
    val watchdogGraceSeconds: Int = 30,
    // ALRM-11: how each alarm sounds — sound, volume and vibrate, each per alarm. The default
    // sounds differ so the two alarms are told apart by ear out of the box (WATCH-2).
    val cryAlarmSound: String = SOUND_RISING_CHIME,
    val feedAlarmSound: String = SOUND_LOW_PULSE,
    val cryAlarmVolume: Double = 0.85, // 0..1, on top of the phone's alarm stream
    val feedAlarmVolume: Double = 0.85,
    val cryAlarmVibrate: Boolean = true,
    val feedAlarmVibrate: Boolean = true,
) {
    fun toJson(): String = JSONObject()
        .put("muted", muted)
        .put("alarmEnabled", alarmEnabled)
        .put("alarmSensitivity", alarmSensitivity)
        .put("alarmScheduleMode", alarmScheduleMode)
        .put("alarmWindowStartMinutes", alarmWindowStartMinutes)
        .put("alarmWindowEndMinutes", alarmWindowEndMinutes)
        .put("watchdogEnabled", watchdogEnabled)
        .put("watchdogGraceSeconds", watchdogGraceSeconds)
        .put("cryAlarmSound", cryAlarmSound)
        .put("feedAlarmSound", feedAlarmSound)
        .put("cryAlarmVolume", cryAlarmVolume)
        .put("feedAlarmVolume", feedAlarmVolume)
        .put("cryAlarmVibrate", cryAlarmVibrate)
        .put("feedAlarmVibrate", feedAlarmVibrate)
        .toString()

    companion object {
        const val SCHEDULE_ALWAYS = "always"
        const val SCHEDULE_WINDOW = "window"

        // ALRM-2: the sensitivity scale. 1 needs loud crying, 10 alarms on quiet crying.
        const val SENSITIVITY_MIN = 1
        const val SENSITIVITY_MAX = 10
        const val SENSITIVITY_DEFAULT = 5

        // ALRM-4: never all the way down — a stored volume of zero would be a silent alarm.
        const val VOLUME_MIN = 0.2
        const val VOLUME_MAX = 1.0

        // WATCH-1: grace bounds — a zero grace would alarm on every one-second audio hiccup.
        const val GRACE_MIN_SECONDS = 5
        const val GRACE_MAX_SECONDS = 120

        // ALRM-11: calm → urgent.
        const val SOUND_SOFT_CHIME = "soft-chime"
        const val SOUND_RISING_CHIME = "rising-chime"
        const val SOUND_LOW_PULSE = "low-pulse"
        const val SOUND_URGENT_BEEP = "urgent-beep"
        const val SOUND_SIREN = "siren"

        val ALARM_SOUNDS = listOf(
            SOUND_SOFT_CHIME,
            SOUND_RISING_CHIME,
            SOUND_LOW_PULSE,
            SOUND_URGENT_BEEP,
            SOUND_SIREN,
        )

        fun fromJson(json: String?): Settings {
            if (json == null) return Settings()
            return try {
                val v = JSONObject(json)
                // ALRM-6: settings written before volume/vibrate were per-alarm had one shared
                // "alarmVolume"/"alarmVibrate" — an old value seeds both alarms rather than
                // being lost.
                val legacyVolume = v.optDouble("alarmVolume", 0.85)
                val legacyVibrate = v.optBoolean("alarmVibrate", true)
                Settings(
                    muted = v.optBoolean("muted", false),
                    alarmEnabled = v.optBoolean("alarmEnabled", false),
                    alarmSensitivity = v.optInt("alarmSensitivity", legacySensitivity(v))
                        .coerceIn(SENSITIVITY_MIN, SENSITIVITY_MAX),
                    alarmScheduleMode = v.optString("alarmScheduleMode", SCHEDULE_ALWAYS),
                    alarmWindowStartMinutes = v.optInt("alarmWindowStartMinutes", 19 * 60)
                        .coerceIn(0, 24 * 60 - 1),
                    alarmWindowEndMinutes = v.optInt("alarmWindowEndMinutes", 7 * 60)
                        .coerceIn(0, 24 * 60 - 1),
                    watchdogEnabled = v.optBoolean("watchdogEnabled", false),
                    // Clamped like sensitivity: stored data (old versions, hand edits) must never
                    // yield a watchdog that fires on every hiccup or an alarm that plays silently.
                    watchdogGraceSeconds = v.optInt("watchdogGraceSeconds", 30)
                        .coerceIn(GRACE_MIN_SECONDS, GRACE_MAX_SECONDS),
                    cryAlarmSound = v.optString("cryAlarmSound", SOUND_RISING_CHIME),
                    feedAlarmSound = v.optString("feedAlarmSound", SOUND_LOW_PULSE),
                    cryAlarmVolume = v.optDouble("cryAlarmVolume", legacyVolume)
                        .coerceIn(VOLUME_MIN, VOLUME_MAX),
                    feedAlarmVolume = v.optDouble("feedAlarmVolume", legacyVolume)
                        .coerceIn(VOLUME_MIN, VOLUME_MAX),
                    cryAlarmVibrate = v.optBoolean("cryAlarmVibrate", legacyVibrate),
                    feedAlarmVibrate = v.optBoolean("feedAlarmVibrate", legacyVibrate),
                )
            } catch (_: Exception) {
                Settings()
            }
        }

        /**
         * ALRM-6: settings written before the sensitivity slider carried a dB threshold
         * (3 = most sensitive .. 24 = least). Map it onto the 1..10 scale rather than lose the
         * user's tuning. The old scale is frozen history — these constants must never track
         * whatever the live sensitivity mapping becomes.
         */
        private fun legacySensitivity(v: JSONObject): Int {
            if (!v.has("alarmThresholdDb")) return SENSITIVITY_DEFAULT
            return ((24.0 - v.optDouble("alarmThresholdDb", 14.0)) / 2.0).roundToInt()
                .coerceIn(SENSITIVITY_MIN, SENSITIVITY_MAX)
        }
    }
}

class AppStore(private val kv: KeyValueStore, private val box: SecretBox) {
    /**
     * The decrypted session, held after the first read.
     *
     * Without this, every caller that wants to know whether we are signed in — the router, the
     * engine, the camera controls — pays a full decrypt. On Android that is a wasted Keystore
     * round-trip. On macOS it is worse: the Keychain guards an item against the specific binary
     * that wrote it, so after an update each of those reads raises its OWN password prompt. Four
     * reads, four prompts, and the app blocked behind them.
     *
     * Read once, hold it, and keep it in step on every write. Nothing outside this class touches
     * the sealed form.
     */
    @Volatile
    private var cachedSession: Session? = null

    @Volatile
    private var sessionRead = false

    private companion object {
        const val KEY_SESSION = "session_v1"
        const val KEY_DEVICE = "device_v1"
        const val KEY_SETTINGS = "settings_v1"
        const val KEY_MONITORING = "monitoring_v1"
        const val KEY_ALARM_VOLUME_PREV = "alarm_volume_prev_v1"
        const val KEY_ALARM_VOLUME_RAISED = "alarm_volume_raised_v1"
        const val KEY_CRY_CALIBRATION = "cry_calibration_v1" // JSON object: camera did → steps
    }

    /**
     * AUTH-6: tokens are only ever stored encrypted. If the Keystore refuses (key invalidated by
     * an OS update or a lock-screen change), drop the session rather than crash or, worse, fall
     * back to plaintext — the user simply signs in again.
     */
    fun saveSession(s: Session) {
        val sealed = try {
            box.seal(Codecs.sessionToJson(s))
        } catch (e: Exception) {
            Log.w("data", "could not encrypt session: ${e.message}", e)
            null
        }
        if (sealed == null) {
            Log.w("data", "the secret store refused the session — not persisting it")
            kv.remove(KEY_SESSION)
            cachedSession = null
            sessionRead = true
            return
        }
        kv.put(KEY_SESSION, sealed)
        cachedSession = s
        sessionRead = true
    }

    fun loadSession(): Session? {
        if (!sessionRead) {
            cachedSession = kv.get(KEY_SESSION)?.let { box.open(it) }?.let { Codecs.sessionFromJson(it) }
            sessionRead = true
        }
        return cachedSession
    }

    fun saveDevice(d: Device) = kv.put(KEY_DEVICE, Codecs.deviceToJson(d))

    fun loadDevice(): Device? = kv.get(KEY_DEVICE)?.let { Codecs.deviceFromJson(it) }

    fun loadSettings(): Settings = Settings.fromJson(kv.get(KEY_SETTINGS))

    fun saveSettings(s: Settings) = kv.put(KEY_SETTINGS, s.toJson())

    /** BG-10: was monitoring running when the process last went away (e.g. a reboot)? */
    fun wasMonitoring(): Boolean = kv.get(KEY_MONITORING) == "true"

    fun setMonitoring(on: Boolean) = kv.put(KEY_MONITORING, on.toString())

    /**
     * ALRM-10: the alarm volume the user had before we raised it, and what we raised it to.
     * Persisted so a process death mid-alarm still puts their phone back the way they left it —
     * and the "raised to" half is what lets us tell our own change from one the user made since,
     * so we never silence the alarm clock they set in the meantime.
     */
    fun alarmVolumeRestore(): Pair<Int, Int>? {
        val previous = kv.get(KEY_ALARM_VOLUME_PREV)?.toIntOrNull() ?: return null
        val raisedTo = kv.get(KEY_ALARM_VOLUME_RAISED)?.toIntOrNull() ?: return null
        return previous to raisedTo
    }

    fun setAlarmVolumeRestore(previous: Int?, raisedTo: Int?) {
        if (previous == null || raisedTo == null) {
            kv.remove(KEY_ALARM_VOLUME_PREV)
            kv.remove(KEY_ALARM_VOLUME_RAISED)
        } else {
            kv.put(KEY_ALARM_VOLUME_PREV, previous.toString())
            kv.put(KEY_ALARM_VOLUME_RAISED, raisedTo.toString())
        }
    }

    /**
     * ALRM-17: the learned crying-alarm tuning, per camera. Plain steps; the bounds and their
     * meaning live in the monitor layer — corrupt or absent data reads as "nothing learned".
     */
    fun cryCalibrationSteps(did: String): Int = try {
        kv.get(KEY_CRY_CALIBRATION)?.let { JSONObject(it).optInt(did, 0) } ?: 0
    } catch (_: Exception) {
        0
    }

    fun saveCryCalibrationSteps(did: String, steps: Int) {
        val all = try {
            JSONObject(kv.get(KEY_CRY_CALIBRATION) ?: "{}")
        } catch (_: Exception) {
            JSONObject() // corrupt store: better to relearn than to fail to save (ALRM-17)
        }
        kv.put(KEY_CRY_CALIBRATION, all.put(did, steps).toString())
    }

    /** AUTH-10: signing out forgets the session AND the selected camera (not settings — and not
     *  the learned tuning, which belongs to the camera, not the account). */
    fun signOut() {
        kv.remove(KEY_SESSION)
        kv.remove(KEY_DEVICE)
        cachedSession = null
        sessionRead = true
    }

    /** CAM-4: switching camera forgets the choice, not the account — the picker comes back. */
    fun clearDevice() = kv.remove(KEY_DEVICE)
}
