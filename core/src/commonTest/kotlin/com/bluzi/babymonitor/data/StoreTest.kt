package com.bluzi.babymonitor.data

import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.Session
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.Test

private class MemoryKv : KeyValueStore {
    val map = mutableMapOf<String, String>()
    override fun get(key: String): String? = map[key]
    override fun put(key: String, value: String) {
        map[key] = value
    }

    override fun remove(key: String) {
        map.remove(key)
    }
}

/** Reversible, visibly-marking fake so tests can prove sealing happened. */
private class MarkingSecretBox : SecretBox {
    override fun seal(plain: String): String = "SEALED:" + plain.reversed()
    override fun open(sealed: String): String? =
        if (sealed.startsWith("SEALED:")) sealed.removePrefix("SEALED:").reversed() else null
}

/** A secret store that says no — an invalidated Keystore key, a Keychain that declines. */
private class RefusingSecretBox : SecretBox {
    override fun seal(plain: String): String? = null
    override fun open(sealed: String): String? = null
}

/** A secret store that fails the other way: by throwing (which the Android Keystore really does). */
private class ThrowingSecretBox : SecretBox {
    override fun seal(plain: String): String = error("keystore key invalidated")
    override fun open(sealed: String): String? = null
}

private fun sampleSession() = Session(
    userId = "100001",
    cUserId = "CU1",
    passToken = "PT-secret",
    serviceToken = "ST-secret",
    ssecurity = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8),
    region = "de",
)

private fun sampleDevice() = Device("did-1", "Nursery", "chuangmi.camera.077ac1", "AA:BB", "192.168.1.50")

class StoreTest {
    private val kv = MemoryKv()
    private val store = AppStore(kv, MarkingSecretBox())

    @Test
    fun `AUTH-5 a saved session round-trips including the ssecurity bytes`() {
        store.saveSession(sampleSession())
        assertEquals(sampleSession(), store.loadSession())
    }

    @Test
    fun `AUTH-6 what hits storage is the sealed form — never plaintext tokens`() {
        store.saveSession(sampleSession())
        val stored = kv.map.values.joinToString()
        assertTrue(stored.contains("SEALED:"))
        assertFalse(stored.contains("PT-secret"))
        assertFalse(stored.contains("ST-secret"))
    }

    @Test
    fun `AUTH-6 a secret store that refuses drops the session — it never crashes and never falls back to plaintext`() {
        // This is not hypothetical. A Keychain that declined to store the token once raised an
        // ObjC exception straight through Kotlin and killed the whole monitor. A storage failure
        // must cost a sign-in, never the monitor.
        val refusing = MemoryKv()
        val store = AppStore(refusing, RefusingSecretBox())
        store.saveSession(sampleSession())

        assertNull(store.loadSession())
        val stored = refusing.map.values.joinToString()
        assertFalse(stored.contains("PT-secret"))
        assertFalse(stored.contains("ST-secret"))
    }

    @Test
    fun `AUTH-6 a secret store that throws drops the session rather than taking the monitor down`() {
        val throwing = MemoryKv()
        val store = AppStore(throwing, ThrowingSecretBox())
        store.saveSession(sampleSession()) // must not propagate

        assertNull(store.loadSession())
        assertFalse(throwing.map.values.joinToString().contains("PT-secret"))
    }

    @Test
    fun `AUTH-13 saveSession reports whether the session actually reached the store`() {
        // The honesty signal every shell relies on: a true means signed in and persisted, a false
        // means authenticated but not stored — and a shell that ignores it reports a phantom sign-in
        // and drops the user back to login with nothing said.
        assertTrue(AppStore(MemoryKv(), MarkingSecretBox()).saveSession(sampleSession()))
        assertFalse(AppStore(MemoryKv(), RefusingSecretBox()).saveSession(sampleSession()))
        assertFalse(AppStore(MemoryKv(), ThrowingSecretBox()).saveSession(sampleSession()))
    }

    @Test
    fun `AUTH-10 signing out forgets session and camera but keeps settings and learned tuning`() {
        store.saveSession(sampleSession())
        store.saveDevice(sampleDevice())
        store.saveSettings(Settings(muted = true, alarmEnabled = true, alarmSensitivity = 7))
        store.saveCryCalibrationSteps(sampleDevice().did, 2)

        store.signOut()

        assertNull(store.loadSession())
        assertNull(store.loadDevice())
        assertEquals(true, store.loadSettings().muted)
        // ALRM-17: the tuning belongs to the camera, not the session — re-signing-in keeps it.
        assertEquals(2, store.cryCalibrationSteps(sampleDevice().did))
    }

    @Test
    fun `CAM-2+3 the chosen camera persists and loads back`() {
        assertNull(store.loadDevice())
        store.saveDevice(sampleDevice())
        assertEquals(sampleDevice(), store.loadDevice())
    }

    @Test
    fun `CAM-4 choosing another camera replaces the stored one`() {
        store.saveDevice(sampleDevice())
        val other = Device("did-2", "Bedroom", "chuangmi.camera.077ac1", "EE:FF", "192.168.1.51")
        store.saveDevice(other)
        assertEquals(other, store.loadDevice())
    }

    @Test
    fun `LIVE-2 the mute state persists across restarts`() {
        assertEquals(false, store.loadSettings().muted) // default: unmuted
        store.saveSettings(store.loadSettings().copy(muted = true))
        assertEquals(true, AppStore(kv, MarkingSecretBox()).loadSettings().muted)
    }

    @Test
    fun `LIVE-18 the picture quality persists and defaults to HD`() {
        // HD out of the box: the picture the camera has is the one to show unless someone says otherwise.
        assertEquals(Settings.QUALITY_HD, store.loadSettings().videoQuality)
        store.saveSettings(store.loadSettings().copy(videoQuality = Settings.QUALITY_SD))
        assertEquals(Settings.QUALITY_SD, AppStore(kv, MarkingSecretBox()).loadSettings().videoQuality)
        store.saveSettings(store.loadSettings().copy(videoQuality = Settings.QUALITY_HD))
        assertEquals(Settings.QUALITY_HD, AppStore(kv, MarkingSecretBox()).loadSettings().videoQuality)
    }

    @Test
    fun `LIVE-18 a quality nobody recognises reads back as HD`() {
        // A settings file from a newer version, or a hand edit, must never end as a quality string
        // handed to the camera that it does not understand — and a feed that then never starts.
        assertEquals(Settings.QUALITY_HD, Settings.fromJson("""{"videoQuality":"ultra"}""").videoQuality)
        assertEquals(Settings.QUALITY_HD, Settings.fromJson("""{"videoQuality":""}""").videoQuality)
        assertEquals(Settings.QUALITY_HD, Settings.fromJson("{}").videoQuality)
        assertEquals(Settings.QUALITY_SD, Settings.fromJson("""{"videoQuality":"sd"}""").videoQuality)
    }

    @Test
    fun `ALRM-1+6 alarm defaults off and its settings persist`() {
        val defaults = store.loadSettings()
        assertEquals(false, defaults.alarmEnabled) // ALRM-1: off by default
        store.saveSettings(defaults.copy(alarmEnabled = true, alarmSensitivity = 8))
        val reloaded = AppStore(kv, MarkingSecretBox()).loadSettings()
        assertEquals(true, reloaded.alarmEnabled)
        assertEquals(8, reloaded.alarmSensitivity)
    }

    @Test
    fun `ALRM-2+6 the sensitivity defaults to the middle and persists`() {
        assertEquals(Settings.SENSITIVITY_DEFAULT, store.loadSettings().alarmSensitivity)
        store.saveSettings(store.loadSettings().copy(alarmSensitivity = 3))
        assertEquals(3, AppStore(kv, MarkingSecretBox()).loadSettings().alarmSensitivity)
    }

    @Test
    fun `ALRM-6 a threshold saved by an older version maps onto the sensitivity scale`() {
        // Settings written before the sensitivity slider existed carry a dB threshold instead.
        kv.put("settings_v1", """{"alarmEnabled":true,"alarmThresholdDb":12.0}""")
        assertEquals(6, store.loadSettings().alarmSensitivity) // the old default lands mid-scale
        kv.put("settings_v1", """{"alarmThresholdDb":3.0}""") // old "most sensitive"
        assertEquals(Settings.SENSITIVITY_MAX, store.loadSettings().alarmSensitivity)
        kv.put("settings_v1", """{"alarmThresholdDb":24.0}""") // old "least sensitive"
        assertEquals(Settings.SENSITIVITY_MIN, store.loadSettings().alarmSensitivity)
    }

    @Test
    fun `ALRM-17 learned tuning persists per camera and survives a restart`() {
        assertEquals(0, store.cryCalibrationSteps("did-1")) // nothing learned yet
        store.saveCryCalibrationSteps("did-1", 2)
        store.saveCryCalibrationSteps("did-2", 1)
        val reloaded = AppStore(kv, MarkingSecretBox())
        assertEquals(2, reloaded.cryCalibrationSteps("did-1"))
        assertEquals(1, reloaded.cryCalibrationSteps("did-2")) // each camera keeps its own
        reloaded.saveCryCalibrationSteps("did-1", 0) // reset touches only its camera
        assertEquals(0, reloaded.cryCalibrationSteps("did-1"))
        assertEquals(1, reloaded.cryCalibrationSteps("did-2"))
    }

    @Test
    fun `ALRM-17 corrupt learned tuning degrades to nothing learned`() {
        kv.put("cry_calibration_v1", "{broken")
        assertEquals(0, store.cryCalibrationSteps("did-1"))
        store.saveCryCalibrationSteps("did-1", 1) // and can be written over
        assertEquals(1, store.cryCalibrationSteps("did-1"))
    }

    @Test
    fun `ALRM-6+7 the alarm schedule persists`() {
        assertEquals(Settings.SCHEDULE_ALWAYS, store.loadSettings().alarmScheduleMode) // default
        store.saveSettings(
            store.loadSettings().copy(
                alarmScheduleMode = Settings.SCHEDULE_WINDOW,
                alarmWindowStartMinutes = 20 * 60,
                alarmWindowEndMinutes = 6 * 60 + 30,
            ),
        )
        val reloaded = AppStore(kv, MarkingSecretBox()).loadSettings()
        assertEquals(Settings.SCHEDULE_WINDOW, reloaded.alarmScheduleMode)
        assertEquals(20 * 60, reloaded.alarmWindowStartMinutes)
        assertEquals(6 * 60 + 30, reloaded.alarmWindowEndMinutes)
    }

    @Test
    fun `WATCH-1+5 watchdog defaults off and its settings persist`() {
        val defaults = store.loadSettings()
        assertEquals(false, defaults.watchdogEnabled) // WATCH-1: off by default
        assertEquals(30, defaults.watchdogGraceSeconds) // WATCH-1: default grace
        store.saveSettings(defaults.copy(watchdogEnabled = true, watchdogGraceSeconds = 15))
        val reloaded = AppStore(kv, MarkingSecretBox()).loadSettings()
        assertEquals(true, reloaded.watchdogEnabled)
        assertEquals(15, reloaded.watchdogGraceSeconds)
    }

    @Test
    fun `ALRM-4+6 a stored alarm volume of zero is floored — a silent alarm is not an alarm`() {
        kv.put("settings_v1", """{"cryAlarmVolume":0.0,"feedAlarmVolume":0.0}""")
        assertEquals(Settings.VOLUME_MIN, store.loadSettings().cryAlarmVolume, 1e-9)
        assertEquals(Settings.VOLUME_MIN, store.loadSettings().feedAlarmVolume, 1e-9)
        kv.put("settings_v1", """{"cryAlarmVolume":7.5,"feedAlarmVolume":7.5}""")
        assertEquals(Settings.VOLUME_MAX, store.loadSettings().cryAlarmVolume, 1e-9)
        assertEquals(Settings.VOLUME_MAX, store.loadSettings().feedAlarmVolume, 1e-9)
        // A zero volume from an older version must be floored too, on both alarms.
        kv.put("settings_v1", """{"alarmVolume":0.0}""")
        assertEquals(Settings.VOLUME_MIN, store.loadSettings().cryAlarmVolume, 1e-9)
        assertEquals(Settings.VOLUME_MIN, store.loadSettings().feedAlarmVolume, 1e-9)
    }

    @Test
    fun `WATCH-1+5 a stored grace of zero cannot make the watchdog fire on every hiccup`() {
        kv.put("settings_v1", """{"watchdogGraceSeconds":0}""")
        assertEquals(Settings.GRACE_MIN_SECONDS, store.loadSettings().watchdogGraceSeconds)
        kv.put("settings_v1", """{"watchdogGraceSeconds":-3}""")
        assertEquals(Settings.GRACE_MIN_SECONDS, store.loadSettings().watchdogGraceSeconds)
        kv.put("settings_v1", """{"watchdogGraceSeconds":99999}""")
        assertEquals(Settings.GRACE_MAX_SECONDS, store.loadSettings().watchdogGraceSeconds)
    }

    @Test
    fun `ALRM-6+7 stored schedule minutes outside a day are clamped into one`() {
        kv.put("settings_v1", """{"alarmWindowStartMinutes":99999,"alarmWindowEndMinutes":-5}""")
        val s = store.loadSettings()
        assertEquals(24 * 60 - 1, s.alarmWindowStartMinutes)
        assertEquals(0, s.alarmWindowEndMinutes)
    }

    @Test
    fun `corrupt stored data degrades to defaults instead of crashing`() {
        kv.put("session_v1", "SEALED:not-json")
        kv.put("device_v1", "{broken")
        kv.put("settings_v1", "{broken")
        assertNull(store.loadSession())
        assertNull(store.loadDevice())
        assertEquals(Settings(), store.loadSettings())
    }

    @Test
    fun `BG-10 the store remembers whether monitoring was running — so a restart can be reported`() {
        assertFalse(store.wasMonitoring()) // nothing claimed before the first run
        store.setMonitoring(true)
        assertTrue(AppStore(kv, MarkingSecretBox()).wasMonitoring()) // survives a process restart
        store.setMonitoring(false)
        assertFalse(AppStore(kv, MarkingSecretBox()).wasMonitoring())
    }

    @Test
    fun `ALRM-10 the pre-alarm volume survives a process death — so the phone is put back as found`() {
        assertNull(store.alarmVolumeRestore()) // nothing raised → nothing to put back
        store.setAlarmVolumeRestore(previous = 0, raisedTo = 4)
        // Both halves survive a restart: what to restore, and what we set (to spot a user change).
        assertEquals(0 to 4, AppStore(kv, MarkingSecretBox()).alarmVolumeRestore())
        store.setAlarmVolumeRestore(null, null)
        assertNull(AppStore(kv, MarkingSecretBox()).alarmVolumeRestore())
    }

    @Test
    fun `AUTH-6 a session that cannot be encrypted is dropped — never written in the clear`() {
        val failingBox = object : SecretBox {
            override fun seal(plain: String): String = throw IllegalStateException("keystore gone")
            override fun open(sealed: String): String? = null
        }
        val brittle = AppStore(kv, failingBox)
        brittle.saveSession(sampleSession()) // must not crash the caller
        assertNull(kv.get("session_v1")) // and must not fall back to plaintext
        assertNull(brittle.loadSession())
    }
}
