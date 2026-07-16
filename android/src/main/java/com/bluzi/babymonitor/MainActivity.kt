package com.bluzi.babymonitor

import android.app.AppOpsManager
import android.app.PictureInPictureParams
import android.content.Context
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Rect
import android.os.Build
import android.os.Bundle
import android.os.Process
import android.util.Rational
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.mutableStateOf
import com.bluzi.babymonitor.ui.App

/** BG-18: the three states the viewer's picture-in-picture button can be in — see [pipAvailability]. */
enum class PipAvailability { AVAILABLE, UNSUPPORTED, PERMISSION_OFF }

/**
 * BG-18: whether picture-in-picture can actually happen right now. Two ways it cannot: the phone has
 * no PiP at all, or the parent has turned it off for this app in Android's settings — an app-op the
 * user controls. Either way there is nothing to float, so the viewer's float button is not shown.
 */
fun pipAvailability(context: Context): PipAvailability {
    if (!context.packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)) {
        return PipAvailability.UNSUPPORTED
    }
    val appOps = context.getSystemService(AppOpsManager::class.java) ?: return PipAvailability.AVAILABLE
    val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        appOps.unsafeCheckOpNoThrow(AppOpsManager.OPSTR_PICTURE_IN_PICTURE, Process.myUid(), context.packageName)
    } else {
        @Suppress("DEPRECATION")
        appOps.checkOpNoThrow(AppOpsManager.OPSTR_PICTURE_IN_PICTURE, Process.myUid(), context.packageName)
    }
    // MODE_DEFAULT is the untouched state, and PiP's default is allow — so only an explicit deny
    // (the user turning it off, which lands as ignored/errored) counts as off. Treating MODE_DEFAULT
    // as "off" would wrongly hide the button on every fresh install.
    return if (mode == AppOpsManager.MODE_ALLOWED || mode == AppOpsManager.MODE_DEFAULT) {
        PipAvailability.AVAILABLE
    } else {
        PipAvailability.PERMISSION_OFF
    }
}

class MainActivity : ComponentActivity() {
    /**
     * BG-18: true while the app is in a picture-in-picture window. The viewer drops its chrome then —
     * a system PiP window is a few centimetres of picture, with no room for controls.
     */
    val inPictureInPicture = mutableStateOf(false)

    /**
     * BG-18: the video's on-screen bounds, reported by the viewer as it is laid out. Handed to the OS
     * as the source rect so the enter-PiP animation has something to scale *from* — a smooth zoom into
     * the corner rather than a cross-fade.
     */
    var pipSourceRect: Rect? = null

    /** BG-18: whether the viewer should offer its float button — the OS supports PiP and it is allowed. */
    val pipAvailable: Boolean
        get() = pipAvailability(this) == PipAvailability.AVAILABLE

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge() // uniform edge-to-edge on every API level; screens pad via insets
        setContent { App() }
    }

    /**
     * BG-18: float the live video into a system picture-in-picture window, on demand — the viewer's
     * button calls this. Deliberately manual: the app never floats itself when the parent simply leaves
     * (that surprised people, and was unreliable under gesture navigation). runCatching so a manufacturer
     * that refuses PiP never crashes the monitor; the audio and the alarm carry on regardless (BG-3).
     */
    fun enterPip() {
        if (pipAvailable && !isInPictureInPictureMode) {
            runCatching { enterPictureInPictureMode(pipParams()) }
        }
    }

    private fun pipParams(): PictureInPictureParams {
        val builder = PictureInPictureParams.Builder()
            .setAspectRatio(Rational(16, 9)) // the C100's picture is 16:9
        pipSourceRect?.let { builder.setSourceRectHint(it) }
        return builder.build()
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        inPictureInPicture.value = isInPictureInPictureMode
    }
}
