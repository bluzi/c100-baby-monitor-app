package com.bluzi.babymonitor.monitor

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import com.bluzi.babymonitor.data.Stores
import com.bluzi.babymonitor.log.Log
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

/**
 * BG-1..BG-6: the foreground service that IS the monitor. Holds wake + Wi-Fi locks so the
 * stream survives screen-off; shows the persistent notification (with the live status —
 * BG-2/WATCH-4) and a Stop action; keeps running regardless of the Activity's fate.
 */
class MonitorService : Service() {
    companion object {
        const val ACTION_START = "com.bluzi.babymonitor.START"
        const val ACTION_RESTART = "com.bluzi.babymonitor.RESTART" // camera switched (CAM-4)
        const val ACTION_STOP = "com.bluzi.babymonitor.STOP"
        const val ACTION_ACK_ALARM = "com.bluzi.babymonitor.ACK_ALARM"
        const val ACTION_PREVIEW_SOUND = "com.bluzi.babymonitor.PREVIEW_SOUND"
        const val EXTRA_SOUND = "sound"
        const val CHANNEL_ID = "monitoring"
        const val NOTIFICATION_ID = 1

        fun start(context: Context, restart: Boolean = false) {
            val intent = Intent(context, MonitorService::class.java)
                .setAction(if (restart) ACTION_RESTART else ACTION_START)
            context.startForegroundService(intent)
        }

        fun stop(context: Context) {
            context.startService(Intent(context, MonitorService::class.java).setAction(ACTION_STOP))
        }

        /** ALRM-11: hear an alarm sound from settings, before you are relying on it at 3am. */
        fun previewSound(context: Context, sound: String) {
            context.startService(
                Intent(context, MonitorService::class.java)
                    .setAction(ACTION_PREVIEW_SOUND)
                    .putExtra(EXTRA_SOUND, sound),
            )
        }
    }

    /**
     * Nothing thrown inside the monitor may take the process down — a crash at 3am is a baby
     * monitor that silently stopped monitoring. Log it and keep the service alive.
     */
    private val crashGuard = CoroutineExceptionHandler { _, e ->
        Log.e("service", "unhandled error in monitoring — service stays up: ${e.message}", e)
    }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO + crashGuard)
    private var ringer: AlarmRinger? = null
    private var engine: MonitorEngine? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Monitoring", NotificationManager.IMPORTANCE_LOW),
        )
        // BG-2 / WATCH-4: keep the persistent notification showing the real feed state.
        scope.launch {
            combine(MonitorHub.status, MonitorHub.cameraName, MonitorHub.running) { s, c, r ->
                Triple(s, c, r)
            }.collect { (_, _, running) ->
                // A throw here (a system-server hiccup in notify()) would kill the collector for
                // the life of the service — the notification would silently freeze on stale text
                // all night. Never let one failed update end the updates.
                runCatching { if (running) nm.notify(NOTIFICATION_ID, buildNotification()) }
                    .onFailure { Log.w("service", "could not update the notification: ${it.message}") }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i("service", "onStartCommand action=${intent?.action}")
        when (intent?.action) {
            ACTION_STOP -> {
                stopMonitoring()
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_ACK_ALARM -> {
                // The alarm notification outlives the process (it is ongoing). If the user taps
                // Acknowledge after a restart there is no engine and no ringer — build one anyway
                // so the tap always silences the alarm and clears the notification.
                val alarm = ringer ?: AlarmRinger(this, scope, Stores.app(this)).also { ringer = it }
                val engineAck = MonitorHub.onAcknowledge
                if (engineAck != null) engineAck() else alarm.acknowledge()
                if (!MonitorHub.running.value) {
                    // Nothing was actually being monitored — don't leave a zombie service behind.
                    stopSelf()
                    return START_NOT_STICKY
                }
                return START_STICKY
            }
            ACTION_PREVIEW_SOUND -> { // ALRM-11
                val sound = intent.getStringExtra(EXTRA_SOUND)
                var previewJob: kotlinx.coroutines.Job? = null
                if (sound != null) {
                    val alarm = ringer ?: AlarmRinger(this, scope, Stores.app(this)).also { ringer = it }
                    previewJob = alarm.preview(sound, MonitorHub.settings.value.alarmVolume)
                }
                // A preview must never start, stop, or otherwise disturb monitoring — and when
                // nothing is being monitored, it must not leave an idle service behind either.
                if (MonitorHub.running.value) return START_STICKY
                previewJob?.invokeOnCompletion { if (!MonitorHub.running.value) stopSelf(startId) }
                    ?: stopSelf(startId)
                return START_NOT_STICKY
            }
            ACTION_RESTART -> {
                engine?.stop()
                engine = null
            }
        }

        goForeground()
        acquireLocks()
        if (ringer == null) {
            ringer = AlarmRinger(this, scope, Stores.app(this)).also {
                // If we were killed mid-alarm, the user's alarm volume is still raised. Put it back.
                it.restoreAlarmVolume()
            }
        }
        if (engine == null) {
            engine = MonitorEngine(Stores.app(this), scope, ringer!!)
        }
        engine?.start() // BG-5: no-op when already running — reopening never restarts the stream
        return START_STICKY
    }

    private fun goForeground() {
        val notification = buildNotification()
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            packageManager.getLaunchIntentForPackage(packageName),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val stop = PendingIntent.getService(
            this,
            1,
            Intent(this, MonitorService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val camera = MonitorHub.cameraName.value.ifEmpty {
            Stores.app(this).loadDevice()?.name ?: "camera"
        }
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.presence_video_online)
            .setContentTitle("Monitoring $camera")
            .setContentText(friendlyStatus(MonitorHub.status.value)) // real state, kept current (BG-2/WATCH-4)
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
    }

    private fun acquireLocks() {
        if (wakeLock == null) {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "babymonitor:stream").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
        if (wifiLock == null) {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "babymonitor:stream").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
    }

    private fun stopMonitoring() {
        engine?.stop()
        engine = null
        ringer?.acknowledge()
        wakeLock?.release()
        wakeLock = null
        wifiLock?.release()
        wifiLock = null
        stopForeground(STOP_FOREGROUND_REMOVE)
    }

    override fun onDestroy() {
        stopMonitoring()
        scope.cancel()
        super.onDestroy()
    }
}
