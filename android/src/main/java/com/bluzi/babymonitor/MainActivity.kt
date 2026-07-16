package com.bluzi.babymonitor

import android.app.PictureInPictureParams
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.util.Rational
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.mutableStateOf
import com.bluzi.babymonitor.ui.App

class MainActivity : ComponentActivity() {
    /**
     * BG-18: true while the app is in a picture-in-picture window. The viewer drops its chrome then —
     * a system PiP window is a few centimetres of picture, with no room for controls.
     */
    val inPictureInPicture = mutableStateOf(false)

    /**
     * The viewer sets this while it is showing a running feed, so switching away floats the baby. It
     * stays false everywhere else (sign-in, the picker), where there is nothing to float. Setting it
     * re-registers the PiP params, so the OS's auto-enter (below) tracks the feed as it comes and goes.
     */
    var pipReady = false
        set(value) {
            field = value
            updatePipParams()
        }

    private val supportsPip: Boolean
        get() = packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge() // uniform edge-to-edge on every API level; screens pad via insets
        setContent { App() }
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // BG-18, the pre-Android-12 path only. From Android 12 the system enters PiP itself the moment
        // the parent leaves (setAutoEnterEnabled, armed in pipParams) — and that is the ONLY thing that
        // works with gesture navigation, where this callback is not delivered for the swipe-up-home
        // gesture. Below 12 there is no auto-enter, so this manual entry is the trigger. Either way:
        // only from a running feed, and runCatching so a manufacturer that refuses PiP never crashes
        // the monitor. Audio and the alarm carry on through the foreground service regardless (BG-3).
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S &&
            supportsPip && pipReady && !isInPictureInPictureMode
        ) {
            runCatching { enterPictureInPictureMode(pipParams()) }
        }
    }

    /**
     * BG-18: keep the OS's picture-in-picture params in step with the feed. On Android 12+ this is what
     * arms **auto-enter** — the system floats the window itself when the parent goes home, by gesture
     * *or* button, without leaning on [onUserLeaveHint] (which gesture navigation does not reliably
     * deliver). Disarmed again the moment the feed stops, so the login and picker screens never float.
     * Below 12 there is nothing to pre-register; [onUserLeaveHint] does the work.
     */
    private fun updatePipParams() {
        if (supportsPip && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            runCatching { setPictureInPictureParams(pipParams()) }
        }
    }

    private fun pipParams(): PictureInPictureParams {
        val builder = PictureInPictureParams.Builder()
            .setAspectRatio(Rational(16, 9)) // the C100's picture is 16:9
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setAutoEnterEnabled(pipReady) // float on leave, but only while a feed is live
        }
        return builder.build()
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        inPictureInPicture.value = isInPictureInPictureMode
    }
}
