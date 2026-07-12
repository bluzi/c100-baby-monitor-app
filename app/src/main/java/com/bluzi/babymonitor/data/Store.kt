package com.bluzi.babymonitor.data

import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.Session
import com.bluzi.babymonitor.xiaomi.base64ToBytes
import com.bluzi.babymonitor.xiaomi.toBase64
import org.json.JSONObject

// Persistence: session (encrypted — AUTH-5/6), selected camera (CAM-2/3), settings
// (LIVE-2, ALRM-6). Pure serialization + seams; Android impls live in AndroidStore.kt.

interface KeyValueStore {
    fun get(key: String): String?
    fun put(key: String, value: String)
    fun remove(key: String)
}

/** Encryption seam for secrets — Android Keystore in production, passthrough fake in tests. */
interface SecretBox {
    fun seal(plain: String): String
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
    // ALRM-11: how each alarm sounds. The two must never be the same (see [withSounds]).
    val cryAlarmSound: String = SOUND_RISING_CHIME,
    val feedAlarmSound: String = SOUND_LOW_PULSE,
    val alarmVolume: Double = 0.85, // 0..1, on top of the phone's alarm stream
    val alarmVibrate: Boolean = true,
) {
    /**
     * ALRM-11: the two alarms must never sound the same — "the baby is crying" and "the monitor is
     * blind" call for different reactions, and at 3am you decide by ear before you can read.
     * Picking a sound for one alarm therefore pushes the other off it.
     */
    fun withSounds(cry: String = cryAlarmSound, feed: String = feedAlarmSound): Settings {
        if (cry != feed) return copy(cryAlarmSound = cry, feedAlarmSound = feed)
        val cryChanged = cry != cryAlarmSound
        val alternative = ALARM_SOUNDS.first { it != cry }
        return if (cryChanged) {
            copy(cryAlarmSound = cry, feedAlarmSound = alternative)
        } else {
            copy(cryAlarmSound = alternative, feedAlarmSound = feed)
        }
    }

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
        .put("alarmVolume", alarmVolume)
        .put("alarmVibrate", alarmVibrate)
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
                val settings = Settings(
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
                    alarmVolume = v.optDouble("alarmVolume", 0.85).coerceIn(VOLUME_MIN, VOLUME_MAX),
                    alarmVibrate = v.optBoolean("alarmVibrate", true),
                )
                // Stored data can be old or hand-edited; never let the two alarms collide (ALRM-11).
                settings.withSounds()
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
            return Math.round((24.0 - v.optDouble("alarmThresholdDb", 14.0)) / 2.0).toInt()
                .coerceIn(SENSITIVITY_MIN, SENSITIVITY_MAX)
        }
    }
}

class AppStore(private val kv: KeyValueStore, private val box: SecretBox) {
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
        try {
            kv.put(KEY_SESSION, box.seal(Codecs.sessionToJson(s)))
        } catch (e: Exception) {
            Log.w("data", "could not encrypt session, not persisting it: ${e.message}", e)
            kv.remove(KEY_SESSION)
        }
    }

    fun loadSession(): Session? =
        kv.get(KEY_SESSION)?.let { box.open(it) }?.let { Codecs.sessionFromJson(it) }

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
    }
}
