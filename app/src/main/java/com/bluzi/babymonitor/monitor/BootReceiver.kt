package com.bluzi.babymonitor.monitor

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.bluzi.babymonitor.data.Stores
import com.bluzi.babymonitor.log.Log

/**
 * BG-10: an overnight OS update reboots the phone and monitoring is gone — the parent would keep
 * sleeping, trusting a monitor that is no longer running. Android does not let a mediaPlayback
 * service start itself from boot, so we do the next best thing: say so, loudly, with a tap that
 * resumes monitoring.
 */
class BootReceiver : BroadcastReceiver() {
    companion object {
        const val CHANNEL_ID = "restart"
        const val NOTIFICATION_ID = 3
    }

    override fun onReceive(context: Context, intent: Intent?) {
        if (intent?.action != Intent.ACTION_BOOT_COMPLETED) return
        if (!Stores.app(context).wasMonitoring()) return // nothing was running — nothing to say

        Log.w("service", "phone restarted while monitoring — telling the user")
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Monitoring stopped", NotificationManager.IMPORTANCE_HIGH),
        )
        val open = PendingIntent.getActivity(
            context,
            3,
            context.packageManager.getLaunchIntentForPackage(context.packageName) ?: Intent(),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        nm.notify(
            NOTIFICATION_ID,
            Notification.Builder(context, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.stat_sys_warning)
                .setContentTitle("Monitoring stopped — the phone restarted")
                .setContentText("Tap to open the app and start monitoring again")
                .setContentIntent(open)
                .setAutoCancel(true)
                .setOngoing(true) // it must not be swiped away unnoticed
                .setCategory(Notification.CATEGORY_ALARM)
                .build(),
        )
    }
}
