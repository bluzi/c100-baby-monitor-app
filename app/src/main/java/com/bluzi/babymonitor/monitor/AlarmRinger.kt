package com.bluzi.babymonitor.monitor

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import com.bluzi.babymonitor.data.AppStore
import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.log.Log
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

enum class AlarmKind { BABY_NOISE, FEED_DOWN }

/**
 * ALRM-4 / ALRM-11 / ALRM-14 / WATCH-3: rings the alarm — on the ALARM audio stream, so it cuts
 * through a muted feed and media volume — until acknowledged from the app or the notification.
 * Each alarm kind has its own sound, volume and vibrate setting, so a parent knows what happened
 * by ear before they can read the screen.
 *
 * This class is the last link between a crying baby and a sleeping parent, so it is paranoid:
 *  - it never throws (a crash here would take monitoring down at the worst possible moment),
 *  - it raises the phone's alarm volume if the user left it down, and puts it back (ALRM-10),
 *  - it reports whether it actually started, so a swallowed alarm can be retried (WATCH-6).
 */
class AlarmRinger(
    private val context: Context,
    private val scope: CoroutineScope,
    private val store: AppStore,
) {
    companion object {
        const val CHANNEL_ID = "alerts"
        const val NOTIFICATION_ID = 2

        /** Alarm volume floor while ringing, as a fraction of the stream's max (ALRM-10). */
        private const val MIN_ALARM_VOLUME_FRACTION = 0.5
    }

    private var job: Job? = null

    private val audio: AudioManager
        get() = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    /**
     * Start ringing [kind]. Returns false when another alarm is already sounding — the caller must
     * then retry later rather than treat this alarm as delivered (WATCH-6).
     */
    @Synchronized
    fun ring(kind: AlarmKind, cameraName: String): Boolean {
        if (MonitorHub.activeAlarm.value != null || job?.isActive == true) return false
        val settings = MonitorHub.settings.value
        val (sound, volume, vibrate) = when (kind) {
            AlarmKind.BABY_NOISE ->
                Triple(settings.cryAlarmSound, settings.cryAlarmVolume, settings.cryAlarmVibrate)
            AlarmKind.FEED_DOWN ->
                Triple(settings.feedAlarmSound, settings.feedAlarmVolume, settings.feedAlarmVibrate)
        }
        Log.w("service", "alarm ringing: $kind sound=$sound volume=$volume")

        // The sound IS the alarm; the notification is a convenience. Start the sound first, and let
        // nothing here throw: a throw after marking the alarm active would leave it "ringing" with
        // no sound and no way to acknowledge — and would block every later alarm, all night.
        raiseAlarmVolume()
        job = scope.launch {
            try {
                playLoop(sound, volume, vibrate, kind)
            } catch (e: CancellationException) {
                throw e // acknowledge cancelling the sound is not a failure
            } catch (e: Throwable) {
                // Even an Error must not escape: it would leave a phantom "ringing" alarm —
                // activeAlarm set, no sound, every later alarm blocked until acknowledged.
                Log.e("service", "alarm sound failed — notification only: ${e.message}", e)
            }
        }
        MonitorHub.activeAlarm.value = kind
        runCatching { postNotification(kind, cameraName) }
            .onFailure { Log.e("service", "could not post the alarm notification (it is still ringing)", it) }
        return true
    }

    @Synchronized
    fun acknowledge() {
        val was = MonitorHub.activeAlarm.value
        if (was != null) Log.w("service", "alarm acknowledged (was $was)")
        job?.cancel()
        job = null
        MonitorHub.activeAlarm.value = null
        stopVibration()
        restoreAlarmVolume()
        runCatching {
            val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.cancel(NOTIFICATION_ID)
        }
    }

    /**
     * ALRM-11: preview a sound from settings — exactly what a real alarm of [kind] would do:
     * the sound at that alarm's volume, vibrating if its vibrate option is on. Stops on its own;
     * never counts as a real alarm. Returns the playing job (null if suppressed) so the service
     * can tidy up after it.
     */
    fun preview(sound: String, volume: Double, vibrate: Boolean, kind: AlarmKind): Job? {
        if (MonitorHub.activeAlarm.value != null) return null // never talk over a real alarm
        Log.i("service", "previewing alarm sound: $sound at volume $volume vibrate=$vibrate")
        return scope.launch {
            if (vibrate) startVibration(kind)
            try {
                runCatching { playOnce(sound, volume) }
                    .onFailure { Log.w("service", "could not preview $sound: ${it.message}", it) }
            } finally {
                // A real alarm may have started mid-preview and own the vibrator now — never cut
                // a real alarm's vibration short.
                if (MonitorHub.activeAlarm.value == null) stopVibration()
            }
        }
    }

    /** Builds an AudioTrack that loops one cycle of the sound on the ALARM stream, forever. */
    private fun newTrack(pcm: ShortArray): AudioTrack {
        val track = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_ALARM) // cuts through media volume and mute
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                    .build(),
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(ALARM_SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .build(),
            )
            .setBufferSizeInBytes(pcm.size * 2)
            .setTransferMode(AudioTrack.MODE_STATIC)
            .build()
        track.write(pcm, 0, pcm.size)
        return track
    }

    private suspend fun playLoop(sound: String, volume: Double, vibrate: Boolean, kind: AlarmKind) {
        val pcm = alarmPcm(sound)
        val track = newTrack(pcm)
        if (vibrate) startVibration(kind)
        try {
            track.setLoopPoints(0, pcm.size, -1) // -1: loop until we stop it
            track.setVolume(0f)
            track.play()
            // ALRM-14: rise from gentle to the chosen volume over the first few seconds.
            var elapsed = 0L
            while (currentCoroutineContext().isActive) {
                val gain = (volume * alarmRampGain(elapsed)).toFloat().coerceIn(0f, 1f)
                track.setVolume(gain)
                delay(RAMP_STEP_MS)
                elapsed += RAMP_STEP_MS
            }
        } finally {
            runCatching { track.stop() }
            runCatching { track.release() }
            stopVibration()
        }
    }

    private suspend fun playOnce(sound: String, volume: Double) {
        val pcm = alarmPcm(sound)
        val track = newTrack(pcm)
        try {
            track.setVolume(volume.toFloat().coerceIn(0f, 1f))
            track.play()
            delay(pcm.size * 1000L / ALARM_SAMPLE_RATE)
        } finally {
            runCatching { track.stop() }
            runCatching { track.release() }
        }
    }

    // --- vibration (ALRM-11) ---------------------------------------------------

    private val vibrator: Vibrator?
        get() = runCatching {
            if (Build.VERSION.SDK_INT >= 31) {
                val manager = context.getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as VibratorManager
                manager.defaultVibrator
            } else {
                @Suppress("DEPRECATION")
                context.getSystemService(Context.VIBRATOR_SERVICE) as Vibrator
            }
        }.getOrNull()

    private fun startVibration(kind: AlarmKind) {
        val v = vibrator ?: return
        if (!v.hasVibrator()) return
        // Distinct patterns, like the sounds: a phone face-down on a mattress is felt, not heard.
        val pattern = when (kind) {
            AlarmKind.BABY_NOISE -> longArrayOf(0, 400, 200, 400, 1000)
            AlarmKind.FEED_DOWN -> longArrayOf(0, 120, 100, 120, 100, 120, 1400)
        }
        runCatching {
            v.vibrate(
                VibrationEffect.createWaveform(pattern, 0), // 0: repeat from the start, forever
                AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM).build(),
            )
        }
    }

    private fun stopVibration() {
        runCatching { vibrator?.cancel() }
    }

    // --- the phone's own alarm volume (ALRM-10) --------------------------------

    private fun raiseAlarmVolume() {
        try {
            val am = audio
            val max = am.getStreamMaxVolume(AudioManager.STREAM_ALARM)
            val current = am.getStreamVolume(AudioManager.STREAM_ALARM)
            val floor = Math.max(1, (max * MIN_ALARM_VOLUME_FRACTION).toInt())
            if (current < floor) {
                store.setAlarmVolumeRestore(previous = current, raisedTo = floor)
                am.setStreamVolume(AudioManager.STREAM_ALARM, floor, 0)
                Log.w("service", "alarm volume was $current/$max — raised to $floor to be audible")
            }
        } catch (e: Exception) {
            // Do Not Disturb without policy access, or an OEM that forbids it. The sound still
            // plays at whatever volume the user has; never let this stop the alarm.
            Log.w("service", "could not raise the alarm volume: ${e.message}")
        }
    }

    /** Put the user's alarm volume back — also on the first run after a mid-alarm process death. */
    fun restoreAlarmVolume() {
        val (previous, raisedTo) = store.alarmVolumeRestore() ?: return
        store.setAlarmVolumeRestore(null, null)
        try {
            val current = audio.getStreamVolume(AudioManager.STREAM_ALARM)
            if (current != raisedTo) {
                // The user has since set the alarm volume themselves. Theirs wins — putting our old
                // value back could silence the alarm clock they just set (ALRM-10).
                Log.i("service", "alarm volume changed by the user ($current) — leaving it alone")
                return
            }
            audio.setStreamVolume(AudioManager.STREAM_ALARM, previous, 0)
            Log.i("service", "alarm volume restored to $previous")
        } catch (e: Exception) {
            Log.w("service", "could not restore the alarm volume: ${e.message}")
        }
    }

    private fun postNotification(kind: AlarmKind, cameraName: String) {
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Alarms", NotificationManager.IMPORTANCE_HIGH),
        )
        val open = PendingIntent.getActivity(
            context,
            0,
            context.packageManager.getLaunchIntentForPackage(context.packageName) ?: Intent(),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val ack = PendingIntent.getService(
            context,
            2,
            Intent(context, MonitorService::class.java).setAction(MonitorService.ACTION_ACK_ALARM),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val (title, text) = when (kind) {
            AlarmKind.BABY_NOISE -> "The baby is crying" to
                "$cameraName heard crying"
            AlarmKind.FEED_DOWN -> "Feed unavailable" to
                "No live audio from $cameraName — check the camera and network"
        }
        val notification = Notification.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true) // rings until acknowledged — not swipeable away
            .setCategory(Notification.CATEGORY_ALARM)
            .addAction(Notification.Action.Builder(null, "Acknowledge", ack).build())
            .build()
        nm.notify(NOTIFICATION_ID, notification)
    }
}

private const val RAMP_STEP_MS = 250L
