package com.bluzi.babymonitor

import android.app.Application
import com.bluzi.babymonitor.log.Log

/**
 * Wires the pure [Log] facade to `android.util.Log` before anything else in the process runs
 * (the monitoring service lives in the same process). Everything logs under the single Android
 * tag "BabyMonitor" with the subsystem in the message — filter with `adb logcat -s BabyMonitor`.
 */
class BabyMonitorApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Log.install { level, tag, message, error ->
            val line = "[$tag] $message"
            when (level) {
                Log.Level.DEBUG -> android.util.Log.d(ANDROID_TAG, line)
                Log.Level.INFO -> android.util.Log.i(ANDROID_TAG, line)
                Log.Level.WARN ->
                    if (error != null) android.util.Log.w(ANDROID_TAG, line, error)
                    else android.util.Log.w(ANDROID_TAG, line)
                Log.Level.ERROR ->
                    if (error != null) android.util.Log.e(ANDROID_TAG, line, error)
                    else android.util.Log.e(ANDROID_TAG, line)
            }
        }
        Log.i("app", "process start")
    }

    private companion object {
        const val ANDROID_TAG = "BabyMonitor"
    }
}
