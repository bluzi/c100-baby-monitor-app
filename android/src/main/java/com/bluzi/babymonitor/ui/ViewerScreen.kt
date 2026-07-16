package com.bluzi.babymonitor.ui

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.content.pm.ActivityInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings as AndroidSettings
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import com.bluzi.babymonitor.MainActivity
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.bluzi.babymonitor.data.AppStore
import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.monitor.AlarmKind
import com.bluzi.babymonitor.monitor.CameraControl
import com.bluzi.babymonitor.monitor.MonitorHub
import com.bluzi.babymonitor.monitor.MonitorService
import com.bluzi.babymonitor.monitor.STATUS_LIVE
import com.bluzi.babymonitor.monitor.VideoSurface
import com.bluzi.babymonitor.monitor.effectiveThresholdDb
import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.NightVisionMode
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// LIVE-1..9, BG-5, CAM-4, ALRM/WATCH acknowledge: the live feed and its controls.

private tailrec fun android.content.Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}

/** LIVE-13: is the phone on a network the camera could be on (Wi-Fi, or wired via a dock)? */
private fun onLocalNetwork(cm: ConnectivityManager): Boolean {
    val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
    return caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
        caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
}

@Composable
fun ViewerScreen(
    store: AppStore,
    device: Device,
    onDeviceChanged: (Device) -> Unit,
    onSignOut: () -> Unit,
    onSessionExpired: (String) -> Unit,
) {
    val context = LocalContext.current
    val status by MonitorHub.status.collectAsState()
    val level by MonitorHub.level.collectAsState()
    val running by MonitorHub.running.collectAsState()
    val cameraName by MonitorHub.cameraName.collectAsState()
    val activeAlarm by MonitorHub.activeAlarm.collectAsState()
    // Settings live in the hub (single source); the store is just persistence.
    val settings by MonitorHub.settings.collectAsState()

    val scope = rememberCoroutineScope()
    var showSettings by remember { mutableStateOf(false) }
    var showCameras by remember { mutableStateOf(false) }
    var showSignOut by remember { mutableStateOf(false) }
    var showStop by remember { mutableStateOf(false) }
    var showAbout by remember { mutableStateOf(false) }

    // LIVE-10: night vision — the camera's own mode, read on open, written on change.
    var showNightVision by remember { mutableStateOf(false) }
    var nightVision by remember { mutableStateOf<NightVisionMode?>(null) }
    var nightVisionBusy by remember { mutableStateOf(false) }
    var nightVisionError by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(device.did) {
        nightVision = null // don't show the previous camera's mode while fetching
        nightVision = try {
            CameraControl.getNightVision(store)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            null // camera offline / not readable — control still works, just shows unknown
        }
    }

    // LIVE-9: the live feed is landscape only — lock while this screen shows, release on leave.
    // SENSOR_LANDSCAPE, not LANDSCAPE: whichever way the phone is held at 3am is the right way.
    DisposableEffect(Unit) {
        val activity = context.findActivity()
        activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        onDispose { activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED }
    }

    // BG-18: keep the baby visible in a picture-in-picture window when the parent switches away from a
    // running feed. `pipReady` gates it on the feed actually running (so switching away from a
    // connecting/empty viewer does not float a black tile) AND on the parent leaving it on (BG-19);
    // the activity does the OS check and never floats where PiP is unsupported. In PiP the chrome is
    // dropped — the window is too small for it.
    val pipActivity = remember(context) { context.findActivity() as? MainActivity }
    DisposableEffect(pipActivity, running, settings.pipEnabled) {
        pipActivity?.pipReady = running && settings.pipEnabled
        onDispose { pipActivity?.pipReady = false }
    }
    val inPip = pipActivity?.inPictureInPicture?.value ?: false

    // LIVE-11: the controls are toggled by tapping the VIDEO, and by nothing else.
    // They never hide on their own — watching the room should not cost you a tap to get them back.
    var controlsVisible by remember { mutableStateOf(true) }

    /** A tap on the video (LIVE-11): show the controls if hidden, hide them if shown. */
    fun toggleControls() {
        controlsVisible = !controlsVisible
    }

    /** A tap on a control: use it, and never let it hide the row under the user's finger. */
    fun poke() {
        controlsVisible = true
    }

    // LIVE-9: the system bars hide with the controls (video truly fills the screen).
    val view = LocalView.current
    LaunchedEffect(controlsVisible) {
        val window = view.context.findActivity()?.window ?: return@LaunchedEffect
        val controller = WindowCompat.getInsetsController(window, view)
        controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        if (!controlsVisible) {
            controller.hide(WindowInsetsCompat.Type.systemBars())
        } else {
            controller.show(WindowInsetsCompat.Type.systemBars())
        }
    }
    DisposableEffect(Unit) {
        onDispose {
            view.context.findActivity()?.window?.let {
                WindowCompat.getInsetsController(it, view).show(WindowInsetsCompat.Type.systemBars())
            }
        }
    }

    // LIVE-14: while the feed is live, the screen stays awake — watching the baby must never end
    // in a sleeping screen. Anything else (connecting, error) sleeps normally to spare the battery.
    DisposableEffect(status == STATUS_LIVE) {
        val window = view.context.findActivity()?.window
        if (status == STATUS_LIVE) window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        onDispose { window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }
    }

    // LIVE-13: without Wi-Fi the camera is unreachable — track it live, not just on open.
    val connectivity = remember { context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager }
    var onWifi by remember { mutableStateOf(onLocalNetwork(connectivity)) }
    DisposableEffect(Unit) {
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .addTransportType(NetworkCapabilities.TRANSPORT_ETHERNET)
            .build()
        // Callbacks arrive sequentially on one handler thread; track the matching networks so
        // losing one of two (Wi-Fi + a dock) never shows a false warning — a warning that can
        // be wrong is a warning that gets ignored.
        val localNetworks = mutableSetOf<Network>()
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                localNetworks.add(network)
                onWifi = true
            }

            override fun onLost(network: Network) {
                localNetworks.remove(network)
                onWifi = localNetworks.isNotEmpty()
            }
        }
        connectivity.registerNetworkCallback(request, callback)
        onDispose { connectivity.unregisterNetworkCallback(callback) }
    }

    // Alarms only *notify* with permission; warn when it's denied (alarms still sound).
    var notificationsDenied by remember { mutableStateOf(false) }
    val notifPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted -> notificationsDenied = !granted }
    LaunchedEffect(Unit) {
        if (Build.VERSION.SDK_INT >= 33) notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    // BG-9: without a battery-optimisation exemption, Doze can suspend the stream *and* the
    // watchdog overnight — the monitor would go quiet with nothing to show for it.
    val power = remember { context.getSystemService(Context.POWER_SERVICE) as PowerManager }
    var batteryExempt by remember { mutableStateOf(power.isIgnoringBatteryOptimizations(context.packageName)) }
    val batteryPrompt = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { batteryExempt = power.isIgnoringBatteryOptimizations(context.packageName) }

    // BG-5: opening the live feed starts (or joins) monitoring — including a warm reopen after the
    // notification's Stop. Switching cameras restarts it (CAM-4). Starting is a no-op when the
    // stream is already running, so reopening never interrupts it.
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner, device.did) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_START) {
                MonitorHub.applySettings(store.loadSettings())
                MonitorService.start(context)
                batteryExempt = power.isIgnoringBatteryOptimizations(context.packageName)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // BG-8: the session died while monitoring — no retry can fix it, so send the user to sign-in.
    val sessionExpired by MonitorHub.sessionExpired.collectAsState()
    LaunchedEffect(sessionExpired) {
        if (sessionExpired) {
            MonitorHub.sessionExpired.value = false
            onSessionExpired("Your session expired — please sign in again.")
        }
    }

    fun saveSettings(next: Settings) {
        store.saveSettings(next) // LIVE-2 / ALRM-6 / WATCH-5
        MonitorHub.applySettings(next) // effective immediately (ALRM-2/7/8, WATCH-1)
    }

    val actions = viewerActions(
        muted = settings.muted,
        running = running,
        status = status,
        nightVision = nightVision,
        onToggleMute = { saveSettings(settings.copy(muted = !settings.muted)); poke() },
        onResume = { MonitorService.start(context); poke() },
        onNightVision = { showNightVision = true; poke() },
        onSettings = { showSettings = true },
        onStop = { showStop = true; poke() },
    )

    val alarmBanner: (@Composable (Modifier) -> Unit)? = activeAlarm?.let { kind ->
        { modifier ->
            AlarmBanner(
                text = when (kind) {
                    AlarmKind.BABY_NOISE -> "Baby noise detected"
                    AlarmKind.FEED_DOWN -> "Feed unavailable"
                },
                onAcknowledge = { MonitorHub.acknowledge() },
                modifier = modifier,
            )
        }
    }

    // ALRM-15: after a crying alarm is acknowledged, ask — in the alarm banner's spot, but only
    // while no alarm is ringing: a live alarm always outranks a question about a finished one.
    val pendingFeedback by MonitorHub.pendingCryFeedback.collectAsState()
    val feedbackBanner: (@Composable (Modifier) -> Unit)? =
        if (pendingFeedback != null && activeAlarm == null) {
            { modifier ->
                CryFeedbackBanner(
                    onAnswer = { MonitorHub.submitCryFeedback(it) },
                    onDismiss = { MonitorHub.dismissCryFeedback() },
                    modifier = modifier,
                )
            }
        } else {
            null
        }
    val banner = alarmBanner ?: feedbackBanner

    // ALRM-12: the trigger mark reflects what would actually alarm — dial plus learned tuning.
    val calibrationSteps by MonitorHub.calibrationSteps.collectAsState()
    val thresholdDb = effectiveThresholdDb(settings.alarmSensitivity, calibrationSteps).toFloat()

    // Anything that would quietly weaken the monitor gets said out loud, on the main screen.
    val warnings = buildList {
        if (!onWifi) { // LIVE-13: without Wi-Fi nothing else on this screen can work
            add(
                MonitorWarning("No Wi-Fi — the camera can only be reached on its own network. Tap to turn Wi-Fi on.") {
                    context.startActivity(
                        if (Build.VERSION.SDK_INT >= 29) {
                            Intent(AndroidSettings.Panel.ACTION_WIFI) // the quick toggle panel
                        } else {
                            Intent(AndroidSettings.ACTION_WIFI_SETTINGS)
                        },
                    )
                },
            )
        }
        if (!batteryExempt) { // BG-9
            add(
                MonitorWarning("Battery saving can stop monitoring overnight. Tap to allow it to run.") {
                    batteryPrompt.launch(
                        Intent(AndroidSettings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                            .setData(Uri.parse("package:${context.packageName}")),
                    )
                },
            )
        }
        if (notificationsDenied) {
            add(
                MonitorWarning("Notifications are off — alarms will sound but won't show. Tap to fix.") {
                    context.startActivity(
                        Intent(AndroidSettings.ACTION_APP_NOTIFICATION_SETTINGS)
                            .putExtra(AndroidSettings.EXTRA_APP_PACKAGE, context.packageName),
                    )
                },
            )
        }
    }
    val notice: (@Composable () -> Unit)? = if (warnings.isEmpty()) null else {
        { MonitorWarnings(warnings) }
    }

    val videoSurface: @Composable (Modifier) -> Unit = { modifier ->
        AndroidView(
            factory = { ctx ->
                SurfaceView(ctx).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) {
                            VideoSurface.surface = holder.surface
                        }

                        override fun surfaceChanged(holder: SurfaceHolder, f: Int, w: Int, h: Int) {
                            VideoSurface.surface = holder.surface
                        }

                        override fun surfaceDestroyed(holder: SurfaceHolder) {
                            VideoSurface.surface = null // audio keeps running (LIVE-7)
                        }
                    })
                    // BG-18: report the picture's on-screen bounds to the activity, so the OS can scale
                    // the swipe-to-home transition from them — the difference between reliable and flaky
                    // auto-PiP under gesture navigation.
                    addOnLayoutChangeListener { v, _, _, _, _, _, _, _, _ ->
                        val bounds = android.graphics.Rect()
                        if (v.getGlobalVisibleRect(bounds)) {
                            (ctx.findActivity() as? MainActivity)?.pipSourceRect = bounds
                        }
                    }
                }
            },
            modifier = modifier,
        )
    }

    // LIVE-15: which build is running — the OTA pipeline makes "did the update land?" a real
    // question. Read from the package, so it always matches what is actually installed.
    val appVersion = remember {
        runCatching { context.packageManager.getPackageInfo(context.packageName, 0).versionName }
            .getOrNull().orEmpty()
    }

    ViewerContent(
        cameraName, status, settings.muted, level, thresholdDb, settings.alarmEnabled,
        // BG-18: in a PiP window there is only room for the picture — no chrome, no banner.
        actions, if (inPip) null else banner, notice,
        controlsVisible && !inPip, videoSurface,
        menu = {
            OverlayMenu(
                onCameras = { showCameras = true },
                onSignOut = { showSignOut = true },
                onAbout = { showAbout = true },
            )
        },
        onToggleControls = ::toggleControls, onPoke = ::poke,
    )

    if (showSettings) {
        SettingsDialog(
            settings = settings,
            onChange = ::saveSettings,
            onPreviewSound = { sound, volume, vibrate, kind -> // ALRM-11
                MonitorService.previewSound(context, sound, volume, vibrate, kind)
            },
            onDismiss = { showSettings = false },
        )
    }
    if (showCameras) { // CAM-4
        AlertDialog(
            onDismissRequest = { showCameras = false },
            confirmButton = { TextButton(onClick = { showCameras = false }) { Text("Cancel") } },
            title = { Text("Switch camera") },
            text = {
                CameraList(
                    store = store,
                    currentDid = device.did,
                    onSelect = { cam ->
                        showCameras = false
                        if (cam.did != device.did) { // same camera: never restart a healthy stream
                            store.saveDevice(cam)
                            onDeviceChanged(cam)
                            MonitorService.start(context, restart = true)
                        }
                    },
                    onSessionExpired = { message ->
                        showCameras = false
                        onSessionExpired(message) // AUTH-8: back to sign-in, not a dead end
                    },
                )
            },
        )
    }
    if (showAbout) { // LIVE-15
        AboutDialog(version = appVersion, onDismiss = { showAbout = false })
    }
    if (showStop) { // BG-11
        ConfirmStopDialog(
            onConfirm = {
                showStop = false
                MonitorService.stop(context)
            },
            onDismiss = { showStop = false },
        )
    }
    if (showSignOut) {
        ConfirmSignOutDialog(
            onConfirm = {
                showSignOut = false
                onSignOut() // AUTH-10
            },
            onDismiss = { showSignOut = false },
        )
    }
    if (showNightVision) { // LIVE-10
        NightVisionDialog(
            current = nightVision,
            error = nightVisionError,
            busy = nightVisionBusy,
            onSelect = { mode ->
                nightVisionError = null
                nightVisionBusy = true
                val previous = nightVision
                nightVision = mode // optimistic
                scope.launch {
                    try {
                        CameraControl.setNightVision(store, mode)
                        showNightVision = false
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        nightVision = previous // revert on failure, keep dialog open
                        nightVisionError = e.message ?: "Couldn't reach the camera"
                    } finally {
                        nightVisionBusy = false
                    }
                }
            },
            onDismiss = { showNightVision = false; nightVisionError = null },
        )
    }
}

/**
 * The live feed is landscape only (LIVE-9): video fills the screen; status + level and the
 * overflow menu overlay the top, buttons the bottom, both toggled by tapping the video. The
 * alarm banner is exempt and stays put.
 */
@Composable
private fun ViewerContent(
    cameraName: String,
    status: String,
    muted: Boolean,
    level: Float,
    thresholdDb: Float,
    alarmArmed: Boolean,
    actions: List<ViewerAction>,
    banner: (@Composable (Modifier) -> Unit)?,
    notice: (@Composable () -> Unit)?,
    controlsVisible: Boolean,
    videoSurface: @Composable (Modifier) -> Unit,
    menu: @Composable () -> Unit,
    onToggleControls: () -> Unit,
    onPoke: () -> Unit,
) {
    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
                onClick = onToggleControls, // LIVE-11: tapping the video toggles the controls
            ),
    ) {
        videoSurface(Modifier.fillMaxSize())

        // The overlays sit on soft fades, not hard-edged blocks — the video shows through and
        // the controls still read against any picture.
        val topFade = Brush.verticalGradient(listOf(Color(0xCC000000), Color.Transparent))
        val bottomFade = Brush.verticalGradient(listOf(Color.Transparent, Color(0xCC000000)))
        AnimatedVisibility(
            visible = controlsVisible,
            enter = fadeIn(),
            exit = fadeOut(),
            modifier = Modifier.align(Alignment.TopCenter),
        ) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .background(topFade)
                    // LIVE-11: a tap on the controls is not a tap on the video — it keeps them up.
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null,
                        onClick = onPoke,
                    )
                    .safeDrawingPadding()
                    .padding(start = 20.dp, end = 20.dp, top = 8.dp, bottom = 28.dp),
            ) {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    StatusAndLevel(
                        cameraName, status, muted, level,
                        thresholdDb = thresholdDb,
                        alarmArmed = alarmArmed,
                        onOverlay = true,
                        trailing = menu,
                    )
                    notice?.invoke()
                }
            }
        }
        AnimatedVisibility(
            visible = controlsVisible,
            enter = fadeIn(),
            exit = fadeOut(),
            modifier = Modifier.align(Alignment.BottomCenter),
        ) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .background(bottomFade)
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null,
                        onClick = onPoke, // LIVE-11: never hides the row under the user's finger
                    )
                    .safeDrawingPadding()
                    .padding(top = 24.dp, bottom = 8.dp),
            ) {
                IconButtonRow(actions, Modifier.fillMaxWidth())
            }
        }
        // The alarm banner (or the post-alarm question) is always visible and never auto-hides (LIVE-9).
        banner?.invoke(
            Modifier
                .align(Alignment.Center)
                .safeDrawingPadding()
                .padding(horizontal = 24.dp),
        )
    }
}
