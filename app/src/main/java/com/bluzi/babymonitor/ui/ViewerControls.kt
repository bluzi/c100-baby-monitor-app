package com.bluzi.babymonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.NotificationsActive
import androidx.compose.material.icons.filled.DarkMode
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.filled.VolumeOff
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material.icons.filled.WarningAmber
import androidx.compose.material.icons.outlined.DarkMode
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.shape.CircleShape
import com.bluzi.babymonitor.monitor.LevelMeter
import com.bluzi.babymonitor.monitor.STATUS_MONITOR_FAILED
import com.bluzi.babymonitor.monitor.displayLevelDb
import com.bluzi.babymonitor.monitor.statusLine
import com.bluzi.babymonitor.xiaomi.NightVisionMode
import java.util.Locale

/**
 * One control action, rendered as an icon button (LIVE-9). [active] marks an engaged toggle —
 * the button is drawn latched (filled, attention-coloured) so the icon shows STATE, never
 * "what pressing would do" (LIVE-2).
 */
data class ViewerAction(
    val icon: ImageVector,
    val label: String,
    val active: Boolean = false,
    val onClick: () -> Unit,
)

fun viewerActions(
    muted: Boolean,
    running: Boolean,
    status: String,
    nightVision: NightVisionMode?,
    onToggleMute: () -> Unit,
    onResume: () -> Unit,
    onNightVision: () -> Unit,
    onSettings: () -> Unit,
    onStop: () -> Unit,
): List<ViewerAction> = buildList {
    // APP-3/WATCH-11: a failed monitor keeps `running` true (the watchdog still guards), so it
    // needs its own Resume — the failure must be recoverable right here, not by reopening the app.
    if (!running || status == STATUS_MONITOR_FAILED) {
        add(ViewerAction(Icons.Filled.PlayArrow, "Resume", onClick = onResume))
    }
    // BG-11: a running service can be stopped right here — the notification must not be the only
    // way. A failed monitor is still a running service (the watchdog guards), so it offers both.
    if (running) {
        add(ViewerAction(Icons.Filled.Stop, "Stop monitoring", onClick = onStop))
    }
    add(
        ViewerAction(
            if (muted) Icons.Filled.VolumeOff else Icons.Filled.VolumeUp,
            if (muted) "Muted — tap for sound" else "Mute",
            active = muted, // LIVE-2: muted draws latched, unmistakably a state
            onClick = onToggleMute,
        ),
    )
    // LIVE-10: filled moon = forced on, outline = off/auto/unknown.
    add(
        ViewerAction(
            if (nightVision == NightVisionMode.ON) Icons.Filled.DarkMode else Icons.Outlined.DarkMode,
            "Night vision",
            onClick = onNightVision,
        ),
    )
    add(ViewerAction(Icons.Filled.Tune, "Alerts", onClick = onSettings))
    // LIVE-9: switching camera, signing out, and About are rare — they live in the top-right
    // menu (OverlayMenu), keeping the button row down to what a night actually needs.
}

/** LIVE-9: the less-used actions behind a top-right menu instead of always-visible buttons. */
@Composable
fun OverlayMenu(onCameras: () -> Unit, onSignOut: () -> Unit, onAbout: () -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) {
            Icon(Icons.Filled.MoreVert, contentDescription = "Menu", tint = Color.White)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(text = { Text("Switch camera") }, onClick = { open = false; onCameras() })
            DropdownMenuItem(text = { Text("Sign out") }, onClick = { open = false; onSignOut() })
            DropdownMenuItem(text = { Text("About") }, onClick = { open = false; onAbout() })
        }
    }
}

/** LIVE-15: which build is running — the OTA pipeline makes "did the update land?" a real question. */
@Composable
fun AboutDialog(version: String, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Baby Monitor") },
        text = { Text(if (version.isEmpty()) "Version unknown" else "Version $version") },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

@Composable
fun IconButtonRow(actions: List<ViewerAction>, modifier: Modifier = Modifier) {
    Row(
        modifier,
        // A centred cluster, not a full-width scatter — buttons stay near each other (and the
        // thumb), and the row reads as one control strip.
        horizontalArrangement = Arrangement.spacedBy(28.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        for (action in actions) {
            // LIVE-2: an engaged toggle is drawn latched — filled, attention-coloured — so the
            // state is readable without knowing which way the icon convention goes.
            IconButton(
                onClick = action.onClick,
                modifier = if (action.active) {
                    Modifier.background(MaterialTheme.colorScheme.errorContainer, CircleShape)
                } else {
                    Modifier
                },
            ) {
                Icon(
                    action.icon,
                    contentDescription = action.label,
                    tint = if (action.active) {
                        MaterialTheme.colorScheme.onErrorContainer
                    } else {
                        MaterialTheme.colorScheme.onSurface
                    },
                )
            }
        }
    }
}

/** Status text + the ambient-relative level bar with the alarm threshold on it (LIVE-4/6, ALRM-12). */
@Composable
fun StatusAndLevel(
    cameraName: String,
    status: String,
    muted: Boolean,
    level: Float,
    thresholdDb: Float,
    alarmArmed: Boolean,
    onOverlay: Boolean,
    /** Sits at the end of the status line (the overlay menu) so the two share one row. */
    trailing: (@Composable () -> Unit)? = null,
) {
    val textColor = if (onOverlay) Color.White else MaterialTheme.colorScheme.onSurface
    val subColor = if (onOverlay) Color(0xFFB8C0C8) else MaterialTheme.colorScheme.onSurfaceVariant
    val shown = displayLevelDb(level.toDouble()).toFloat() // LIVE-6: residual flutter reads quiet
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                statusLine(cameraName, status, muted), // APP-3 readable; LIVE-2: muted said in words
                style = MaterialTheme.typography.titleMedium,
                color = textColor,
                modifier = Modifier.weight(1f),
            )
            trailing?.invoke()
        }
        // ALRM-12: mark where the alarm would go off, and colour the bar once the room is past it.
        LevelBar(
            level = shown,
            thresholdDb = if (alarmArmed) thresholdDb else null, // no armed alarm → no line
            onOverlay = onOverlay,
        )
        Text(
            // Locale.ROOT: the app is English-only (UI-2) — digits must not localise either.
            if (alarmArmed && shown >= thresholdDb) {
                "Room level: ${"%.0f".format(Locale.ROOT, shown)} dB — loud enough to alarm"
            } else {
                "Room level: ${"%.0f".format(Locale.ROOT, shown)} dB louder than usual"
            },
            style = MaterialTheme.typography.bodySmall,
            color = subColor,
        )
    }
}

/**
 * The banner shown while an alarm is ringing (ALRM-4 / WATCH-3). Always visible, never
 * auto-hidden; its Acknowledge button silences the alarm.
 */
@Composable
fun AlarmBanner(text: String, onAcknowledge: () -> Unit, modifier: Modifier = Modifier) {
    val onError = MaterialTheme.colorScheme.onError // dark-on-salmon: readable at a glance
    Row(
        // The whole banner acknowledges — at 3am, half-asleep, the most urgent control in the
        // app must be the easiest thing on the screen to hit.
        modifier
            .fillMaxWidth()
            .clickable(onClick = onAcknowledge)
            .background(MaterialTheme.colorScheme.error, RoundedCornerShape(12.dp))
            .padding(horizontal = 16.dp, vertical = 14.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.NotificationsActive, contentDescription = null, tint = onError)
            Box(Modifier.padding(start = 10.dp)) {
                Text(text, color = onError, style = MaterialTheme.typography.titleSmall)
            }
        }
        Text("ACKNOWLEDGE", color = onError, style = MaterialTheme.typography.labelLarge)
    }
}

/**
 * ALRM-15: after a crying alarm is acknowledged, ask whether it was real. Deliberately calm and
 * optional — it informs the learning (ALRM-16), it never demands anything of a half-asleep parent.
 */
@Composable
fun CryFeedbackBanner(onAnswer: (wasCry: Boolean) -> Unit, onDismiss: () -> Unit, modifier: Modifier = Modifier) {
    Column(
        modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(12.dp))
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(
            "Was the baby crying?",
            color = MaterialTheme.colorScheme.onSurface,
            style = MaterialTheme.typography.titleSmall,
        )
        Text(
            "Your answer tunes this camera's alarm.",
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            style = MaterialTheme.typography.bodySmall,
        )
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = { onAnswer(true) }) { Text("YES") }
                TextButton(onClick = { onAnswer(false) }) { Text("NO — FALSE ALARM") }
            }
            TextButton(onClick = onDismiss) {
                Text("SKIP", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

/** Something that would quietly weaken the monitor (BG-9, notifications off), and how to fix it. */
data class MonitorWarning(val text: String, val onClick: () -> Unit)

@Composable
fun MonitorWarnings(warnings: List<MonitorWarning>) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        for (warning in warnings) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable(onClick = warning.onClick)
                    .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(8.dp))
                    .padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(
                    Icons.Filled.WarningAmber,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.secondary,
                )
                Box(Modifier.padding(start = 10.dp)) {
                    Text(
                        warning.text,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                }
            }
        }
    }
}

/** BG-11 guard: a single stray tap must never stop the monitor — always confirm. */
@Composable
fun ConfirmStopDialog(onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Stop monitoring?") },
        text = { Text("Audio and alarms stop until you start monitoring again.") },
        confirmButton = { TextButton(onClick = onConfirm) { Text("Stop") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** AUTH-10 guard: signing out forgets the account and camera — always confirm. */
@Composable
fun ConfirmSignOutDialog(onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Sign out?") },
        text = { Text("This stops monitoring and forgets the account and camera on this phone.") },
        confirmButton = { TextButton(onClick = onConfirm) { Text("Sign out") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
