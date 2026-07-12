package com.bluzi.babymonitor.data

import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.Session
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

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
    fun `AUTH-6 what hits storage is the sealed form, never plaintext tokens`() {
        store.saveSession(sampleSession())
        val stored = kv.map.values.joinToString()
        assertTrue(stored.contains("SEALED:"))
        assertFalse(stored.contains("PT-secret"))
        assertFalse(stored.contains("ST-secret"))
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
    fun `corrupt stored data degrades to defaults instead of crashing`() {
        kv.put("session_v1", "SEALED:not-json")
        kv.put("device_v1", "{broken")
        kv.put("settings_v1", "{broken")
        assertNull(store.loadSession())
        assertNull(store.loadDevice())
        assertEquals(Settings(), store.loadSettings())
    }

    @Test
    fun `BG-10 the store remembers whether monitoring was running, so a restart can be reported`() {
        assertFalse(store.wasMonitoring()) // nothing claimed before the first run
        store.setMonitoring(true)
        assertTrue(AppStore(kv, MarkingSecretBox()).wasMonitoring()) // survives a process restart
        store.setMonitoring(false)
        assertFalse(AppStore(kv, MarkingSecretBox()).wasMonitoring())
    }

    @Test
    fun `ALRM-10 the pre-alarm volume survives a process death, so the phone is put back as found`() {
        assertNull(store.alarmVolumeRestore()) // nothing raised → nothing to put back
        store.setAlarmVolumeRestore(previous = 0, raisedTo = 4)
        // Both halves survive a restart: what to restore, and what we set (to spot a user change).
        assertEquals(0 to 4, AppStore(kv, MarkingSecretBox()).alarmVolumeRestore())
        store.setAlarmVolumeRestore(null, null)
        assertNull(AppStore(kv, MarkingSecretBox()).alarmVolumeRestore())
    }

    @Test
    fun `AUTH-6 a session that cannot be encrypted is dropped, never written in the clear`() {
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
