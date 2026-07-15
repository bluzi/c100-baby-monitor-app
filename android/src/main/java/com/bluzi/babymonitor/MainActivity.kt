package com.bluzi.babymonitor

import android.app.PictureInPictureParams
import android.content.pm.PackageManager
import android.content.res.Configuration
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
     * stays false everywhere else (sign-in, the picker), where there is nothing to float.
     */
    var pipReady = false

    private val supportsPip: Boolean
        get() = packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge() // uniform edge-to-edge on every API level; screens pad via insets
        setContent { App() }
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // BG-18: the parent switched away — keep the baby visible in a floating window over their
        // other work, when the OS supports it. Audio and the alarm carry on through the foreground
        // service regardless (BG-3), so a device without PiP simply keeps monitoring in the background
        // as it always has. runCatching: a manufacturer that refuses PiP must never crash the monitor.
        if (supportsPip && pipReady && !isInPictureInPictureMode) {
            runCatching { enterPictureInPictureMode(pipParams()) }
        }
    }

    private fun pipParams(): PictureInPictureParams =
        PictureInPictureParams.Builder()
            .setAspectRatio(Rational(16, 9)) // the C100's picture is 16:9
            .build()

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        inPictureInPicture.value = isInPictureInPictureMode
    }
}
