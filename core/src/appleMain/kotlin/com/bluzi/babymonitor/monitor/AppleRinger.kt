package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.log.Log
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.alloc
import kotlinx.cinterop.convert
import kotlinx.cinterop.get
import kotlinx.cinterop.memScoped
import kotlinx.cinterop.ptr
import kotlinx.cinterop.usePinned
import kotlinx.cinterop.value
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import platform.AVFAudio.AVAudioEngine
import platform.AVFAudio.AVAudioFormat
import platform.AVFAudio.AVAudioPCMBuffer
import platform.AVFAudio.AVAudioPCMFormatInt16
import platform.AVFAudio.AVAudioPlayerNode
import platform.AVFAudio.AVAudioPlayerNodeBufferLoops
import platform.posix.memcpy

private const val TAG = "service"
private const val RAMP_STEP_MS = 250L

/**
 * The Mac's [Ringer]. ALRM-4 / ALRM-11 / ALRM-14 / WATCH-3: rings until acknowledged, on its own
 * audio engine so a muted feed cannot silence it.
 *
 * The alarm tones are the shared ones — `alarmPcm()` synthesises them in core, so the phone and
 * the Mac make literally the same sound. Nothing here is Mac-specific except how the samples reach
 * a speaker.
 *
 * This is the last link between a crying baby and a sleeping parent, so it is paranoid: it never
 * throws, and it reports whether it actually started so a swallowed alarm can be retried (WATCH-6).
 *
 * A Mac has no vibration motor and no separate alarm-volume stream. That is a real difference from
 * the phone, and it is not papered over: the alarm plays at the volume the user chose (ALRM-11) on
 * the system's output, and the menu bar icon makes a ringing alarm unmistakable (MACOS-1) so a
 * muted Mac still *shows* the alarm.
 */
@OptIn(ExperimentalForeignApi::class)
class AppleRinger(private val scope: CoroutineScope) : Ringer {
    private var job: Job? = null

    override fun ring(kind: AlarmKind, cameraName: String): Boolean {
        if (MonitorHub.activeAlarm.value != null || job?.isActive == true) return false
        val settings = MonitorHub.settings.value
        val sound = when (kind) {
            AlarmKind.BABY_NOISE -> settings.cryAlarmSound
            AlarmKind.FEED_DOWN -> settings.feedAlarmSound
        }
        val volume = when (kind) {
            AlarmKind.BABY_NOISE -> settings.cryAlarmVolume
            AlarmKind.FEED_DOWN -> settings.feedAlarmVolume
        }
        Log.w(TAG, "alarm ringing: $kind sound=$sound volume=$volume camera=$cameraName")

        // The sound IS the alarm. Start it first, and let nothing here throw: a throw after marking
        // the alarm active would leave it "ringing" with no sound and no way to acknowledge — and
        // would block every later alarm, all night.
        job = scope.launch {
            try {
                playLoop(sound, volume)
            } catch (e: CancellationException) {
                throw e // acknowledge cancelling the sound is not a failure
            } catch (e: Throwable) {
                Log.e(TAG, "alarm sound failed: ${e.message}", e)
            }
        }
        MonitorHub.activeAlarm.value = kind
        return true
    }

    override fun acknowledge() {
        val was = MonitorHub.activeAlarm.value
        if (was != null) Log.w(TAG, "alarm acknowledged (was $was)")
        job?.cancel()
        job = null
        MonitorHub.activeAlarm.value = null
    }

    /** ALRM-11: preview a sound exactly as a real alarm of this kind would play it. */
    fun preview(sound: String, volume: Double): Job? {
        if (MonitorHub.activeAlarm.value != null) return null // never talk over a real alarm
        Log.i(TAG, "previewing alarm sound: $sound at volume $volume")
        return scope.launch {
            runCatching { playOnce(sound, volume) }
                .onFailure { Log.w(TAG, "could not preview $sound: ${it.message}", it) }
        }
    }

    private suspend fun playLoop(sound: String, volume: Double) {
        val pcm = alarmPcm(sound)
        val voice = AlarmVoice(pcm, loop = true)
        try {
            voice.start(0f)
            // ALRM-14: rise from gentle to the chosen volume over the first few seconds.
            var elapsed = 0L
            while (currentCoroutineContext().isActive) {
                voice.volume = (volume * alarmRampGain(elapsed)).toFloat().coerceIn(0f, 1f)
                delay(RAMP_STEP_MS)
                elapsed += RAMP_STEP_MS
            }
        } finally {
            voice.release()
        }
    }

    private suspend fun playOnce(sound: String, volume: Double) {
        val pcm = alarmPcm(sound)
        val voice = AlarmVoice(pcm, loop = false)
        try {
            voice.start(volume.toFloat().coerceIn(0f, 1f))
            delay(pcm.size * 1000L / ALARM_SAMPLE_RATE)
        } finally {
            voice.release()
        }
    }
}

/** One alarm tone on its own engine, so nothing about the feed's audio can silence it. */
@OptIn(ExperimentalForeignApi::class)
private class AlarmVoice(private val pcm: ShortArray, private val loop: Boolean) {
    private val engine = AVAudioEngine()
    private val player = AVAudioPlayerNode()

    var volume: Float
        get() = player.volume
        set(value) {
            player.volume = value
        }

    fun start(initialVolume: Float) {
        val format = AVAudioFormat(
            commonFormat = AVAudioPCMFormatInt16,
            sampleRate = ALARM_SAMPLE_RATE.toDouble(),
            channels = 1u,
            interleaved = false,
        ) ?: throw IllegalStateException("alarm: could not build the output format")

        engine.attachNode(player)
        engine.connect(player, engine.mainMixerNode, format)
        memScoped {
            val err = alloc<kotlinx.cinterop.ObjCObjectVar<platform.Foundation.NSError?>>()
            if (!engine.startAndReturnError(err.ptr)) {
                throw IllegalStateException("alarm: engine would not start: ${err.value?.localizedDescription}")
            }
        }

        val buffer = AVAudioPCMBuffer(format, pcm.size.convert())
            ?: throw IllegalStateException("alarm: could not allocate a buffer")
        buffer.frameLength = pcm.size.convert()
        val channel = buffer.int16ChannelData?.get(0)
            ?: throw IllegalStateException("alarm: buffer has no channel data")
        pcm.usePinned { memcpy(channel, it.addressOf(0), (pcm.size * 2).convert()) }

        player.volume = initialVolume
        val options = if (loop) AVAudioPlayerNodeBufferLoops else 0u
        player.scheduleBuffer(buffer, null, options, null)
        player.play()
    }

    fun release() {
        runCatching { player.stop() }
        runCatching { engine.stop() }
    }
}
