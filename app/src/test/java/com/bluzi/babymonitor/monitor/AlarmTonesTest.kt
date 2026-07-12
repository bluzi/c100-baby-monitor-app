package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.dsp.fft
import kotlin.math.abs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AlarmTonesTest {
    private fun peakOf(pcm: ShortArray): Double = pcm.maxOf { abs(it.toInt()) } / 32768.0

    @Test
    fun `ALRM-11 every alarm sound actually makes a sound, and none of them clips`() {
        for (sound in Settings.ALARM_SOUNDS) {
            val pcm = alarmPcm(sound)
            val seconds = pcm.size.toDouble() / ALARM_SAMPLE_RATE
            assertTrue("$sound is silent", peakOf(pcm) > 0.3)
            assertTrue("$sound would clip", peakOf(pcm) <= 1.0)
            // Long enough to be heard, short enough to repeat: repetition is what wakes people.
            assertTrue("$sound cycle is ${seconds}s", seconds in 1.0..4.0)
        }
    }

    @Test
    fun `ALRM-11 every alarm sound ends in a gap, so it repeats instead of droning`() {
        for (sound in Settings.ALARM_SOUNDS) {
            val pcm = alarmPcm(sound)
            val tailStart = (pcm.size * 0.92).toInt()
            val tailPeak = peakOf(pcm.copyOfRange(tailStart, pcm.size))
            assertTrue("$sound does not fall quiet before it loops (tail peak $tailPeak)", tailPeak < 0.1)
        }
    }

    /** The loudest frequency in a window — how the tone actually reads to an ear. */
    private fun dominantHz(pcm: ShortArray): Double {
        val n = 4096
        val re = DoubleArray(n) { if (it < pcm.size) pcm[it] / 32768.0 else 0.0 }
        val im = DoubleArray(n)
        fft(re, im)
        var bestBin = 1
        var bestEnergy = 0.0
        for (bin in 1 until n / 2) {
            val e = re[bin] * re[bin] + im[bin] * im[bin]
            if (e > bestEnergy) {
                bestEnergy = e
                bestBin = bin
            }
        }
        return bestBin * ALARM_SAMPLE_RATE.toDouble() / n
    }

    @Test
    fun `ALRM-11 the alarms are told apart by ear — calm and urgent do not converge on one pitch`() {
        // A parent decides what to do by ear, before they can read the screen.
        val lowPulse = dominantHz(alarmPcm(Settings.SOUND_LOW_PULSE))
        val urgentBeep = dominantHz(alarmPcm(Settings.SOUND_URGENT_BEEP))
        val softChime = dominantHz(alarmPcm(Settings.SOUND_SOFT_CHIME))
        assertTrue("the low pulse must actually be low ($lowPulse Hz)", lowPulse < 350)
        assertTrue("the urgent beep must be high ($urgentBeep Hz)", urgentBeep > 700)
        assertTrue("the soft chime sits between them ($softChime Hz)", softChime in 350.0..1500.0)
    }

    @Test
    fun `ALRM-11 the crying alarm and the feed alarm can never be given the same sound`() {
        val settings = Settings()
        assertNotEquals(settings.cryAlarmSound, settings.feedAlarmSound) // defaults differ

        // Choosing the feed alarm's sound for crying pushes the feed alarm off it.
        val collided = settings.withSounds(cry = settings.feedAlarmSound)
        assertEquals(settings.feedAlarmSound, collided.cryAlarmSound)
        assertNotEquals(collided.cryAlarmSound, collided.feedAlarmSound)

        // And the same the other way round.
        val other = settings.withSounds(feed = settings.cryAlarmSound)
        assertEquals(settings.cryAlarmSound, other.feedAlarmSound)
        assertNotEquals(other.cryAlarmSound, other.feedAlarmSound)
    }

    @Test
    fun `ALRM-11 stored settings that name the same sound twice are repaired on load`() {
        val corrupt = """{"cryAlarmSound":"siren","feedAlarmSound":"siren"}"""
        val loaded = Settings.fromJson(corrupt)
        assertNotEquals(loaded.cryAlarmSound, loaded.feedAlarmSound)
    }

    @Test
    fun `ALRM-14 the alarm starts gentler and reaches full volume within a few seconds`() {
        assertTrue("it must never start silent", alarmRampGain(0) >= 0.3)
        assertTrue(alarmRampGain(0) < alarmRampGain(2_000))
        assertTrue(alarmRampGain(2_000) < alarmRampGain(5_000))
        assertEquals(1.0, alarmRampGain(5_000), 1e-9) // full volume by then
        assertEquals(1.0, alarmRampGain(60_000), 1e-9) // and it stays there
    }

    @Test
    fun `ALRM-6 the alarm sounds, volume and vibrate setting all persist`() {
        val settings = Settings()
            .withSounds(cry = Settings.SOUND_SIREN, feed = Settings.SOUND_SOFT_CHIME)
            .copy(alarmVolume = 0.6, alarmVibrate = false)
        val reloaded = Settings.fromJson(settings.toJson())
        assertEquals(Settings.SOUND_SIREN, reloaded.cryAlarmSound)
        assertEquals(Settings.SOUND_SOFT_CHIME, reloaded.feedAlarmSound)
        assertEquals(0.6, reloaded.alarmVolume, 1e-9)
        assertEquals(false, reloaded.alarmVibrate)
    }
}
