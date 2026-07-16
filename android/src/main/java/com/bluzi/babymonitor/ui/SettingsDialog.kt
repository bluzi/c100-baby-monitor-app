package com.bluzi.babymonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.monitor.AlarmKind
import com.bluzi.babymonitor.monitor.MonitorHub
import com.bluzi.babymonitor.monitor.alarmSoundDescription
import com.bluzi.babymonitor.monitor.alarmSoundLabel
import com.bluzi.babymonitor.monitor.displayLevelDb
import com.bluzi.babymonitor.monitor.effectiveThresholdDb
import java.util.Locale
import kotlin.math.roundToInt

// Locale.ROOT everywhere: the app is English-only (UI-2) — digits must not localise either.
private fun fmtTime(minutes: Int): String = "%02d:%02d".format(Locale.ROOT, minutes / 60, minutes % 60)

/** A section heading; [first] skips the divider that separates a section from the previous one. */
@Composable
private fun SectionLabel(text: String, first: Boolean = false) {
    if (!first) {
        HorizontalDivider(
            color = MaterialTheme.colorScheme.outlineVariant,
            modifier = Modifier.padding(top = 6.dp),
        )
    }
    Text(
        text,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
    )
}

/** Body text that dims with its controls when the owning toggle is off. */
@Composable
private fun dimmedIf(disabled: Boolean, base: Color): Color =
    if (disabled) base.copy(alpha = 0.38f) else base

@Composable
private fun ToggleRow(label: String, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            label,
            color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurface),
            modifier = Modifier.weight(1f).padding(end = 8.dp),
        )
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

/**
 * Alert settings: the crying alarm (ALRM-1/2/7/8) and the feed watchdog (WATCH-1), each with its
 * own sound, volume and vibrate (ALRM-11/14). Every change is pushed through [onChange]
 * immediately so it persists and takes effect right away. Each alarm's sub-settings stay visible
 * but disabled while that alarm is off.
 */
@Composable
fun SettingsDialog(
    settings: Settings,
    onChange: (Settings) -> Unit,
    onPreviewSound: (sound: String, volume: Double, vibrate: Boolean, kind: AlarmKind) -> Unit,
    onDismiss: () -> Unit,
) {
    val liveLevel by MonitorHub.level.collectAsState() // ALRM-12: tune against the real room
    val calibrationSteps by MonitorHub.calibrationSteps.collectAsState() // ALRM-16/17
    // Loudest level since this dialog opened — remember{} dies with the dialog, so reopening resets.
    var peakLevel by remember { mutableFloatStateOf(0f) }
    LaunchedEffect(Unit) {
        MonitorHub.level.collect { if (it > peakLevel) peakLevel = it }
    }
    val alarmOn = settings.alarmEnabled
    // WATCH-9: the feed alarm only ever rings while the crying alarm could — its settings follow.
    val watchdogOn = alarmOn && settings.watchdogEnabled

    AlertDialog(
        onDismissRequest = onDismiss,
        confirmButton = { TextButton(onClick = onDismiss) { Text("Done") } },
        title = { Text("Alert settings") },
        text = {
            Column(
                Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                // --- Crying alarm --------------------------------------------------
                SectionLabel("Crying alarm", first = true)
                ToggleRow("Alarm when the baby cries", alarmOn) { // ALRM-1
                    onChange(settings.copy(alarmEnabled = it))
                }

                Text(
                    "Sensitivity: ${settings.alarmSensitivity}", // ALRM-2: the one detection control
                    color = dimmedIf(!alarmOn, MaterialTheme.colorScheme.onSurface),
                )
                Slider(
                    value = settings.alarmSensitivity.toFloat(),
                    onValueChange = { onChange(settings.copy(alarmSensitivity = it.roundToInt())) },
                    valueRange = Settings.SENSITIVITY_MIN.toFloat()..Settings.SENSITIVITY_MAX.toFloat(),
                    steps = Settings.SENSITIVITY_MAX - Settings.SENSITIVITY_MIN - 1,
                    enabled = alarmOn,
                )
                Text(
                    "Higher alarms on quieter crying. Only crying counts — talking in the next " +
                        "room, TV, fans, rumble and door slams are ignored at every setting.",
                    style = MaterialTheme.typography.bodySmall,
                    color = dimmedIf(!alarmOn, MaterialTheme.colorScheme.onSurfaceVariant),
                )
                // ALRM-12: the live room level with the actual trigger point (dial + learning).
                LevelBar(
                    level = displayLevelDb(liveLevel.toDouble()).toFloat(), // LIVE-6
                    thresholdDb = effectiveThresholdDb(settings.alarmSensitivity, calibrationSteps).toFloat(),
                    dimmed = !alarmOn,
                )
                Text(
                    "Right now: ${"%.0f".format(Locale.ROOT, displayLevelDb(liveLevel.toDouble()))} dB · " +
                        "loudest since opening: ${"%.0f".format(Locale.ROOT, displayLevelDb(peakLevel.toDouble()))} dB",
                    style = MaterialTheme.typography.bodySmall,
                    color = dimmedIf(!alarmOn, MaterialTheme.colorScheme.onSurfaceVariant),
                )
                if (calibrationSteps > 0) { // ALRM-17: learning is never invisible
                    Row(
                        Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            "Tuned $calibrationSteps step${if (calibrationSteps == 1) "" else "s"} " +
                                "less sensitive for this camera, from your answers.",
                            style = MaterialTheme.typography.bodySmall,
                            color = dimmedIf(!alarmOn, MaterialTheme.colorScheme.onSurfaceVariant),
                            modifier = Modifier.weight(1f),
                        )
                        TextButton(onClick = { MonitorHub.resetCalibration() }, enabled = alarmOn) {
                            Text("Reset")
                        }
                    }
                }

                // --- Schedule (ALRM-7) --------------------------------------------
                Text(
                    "Active",
                    style = MaterialTheme.typography.bodyMedium,
                    color = dimmedIf(!alarmOn, MaterialTheme.colorScheme.onSurface),
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(
                        selected = settings.alarmScheduleMode == Settings.SCHEDULE_ALWAYS,
                        onClick = { onChange(settings.copy(alarmScheduleMode = Settings.SCHEDULE_ALWAYS)) },
                        label = { Text("Always") },
                        enabled = alarmOn,
                    )
                    FilterChip(
                        selected = settings.alarmScheduleMode == Settings.SCHEDULE_WINDOW,
                        onClick = { onChange(settings.copy(alarmScheduleMode = Settings.SCHEDULE_WINDOW)) },
                        label = { Text("Between set times") },
                        enabled = alarmOn,
                    )
                }
                if (settings.alarmScheduleMode == Settings.SCHEDULE_WINDOW) {
                    TimeRangeRow(
                        startMinutes = settings.alarmWindowStartMinutes,
                        endMinutes = settings.alarmWindowEndMinutes,
                        enabled = alarmOn,
                        onChange = { s, e ->
                            onChange(settings.copy(alarmWindowStartMinutes = s, alarmWindowEndMinutes = e))
                        },
                    )
                    if (settings.alarmWindowStartMinutes == settings.alarmWindowEndMinutes) {
                        Text(
                            "Start and end match — the alarm is active all day.", // ALRM-7
                            style = MaterialTheme.typography.bodySmall,
                            color = dimmedIf(!alarmOn, MaterialTheme.colorScheme.onSurfaceVariant),
                        )
                    }
                }

                // ALRM-11/14: how this alarm sounds, right below the alarm it belongs to.
                AlarmSoundSettings(
                    sound = settings.cryAlarmSound,
                    volume = settings.cryAlarmVolume,
                    vibrate = settings.cryAlarmVibrate,
                    enabled = alarmOn,
                    kind = AlarmKind.BABY_NOISE,
                    onSound = { onChange(settings.copy(cryAlarmSound = it)) },
                    onVolume = { onChange(settings.copy(cryAlarmVolume = it)) },
                    onVibrate = { onChange(settings.copy(cryAlarmVibrate = it)) },
                    onPreview = onPreviewSound,
                )

                // --- Feed watchdog (WATCH-1/9/10) ---------------------------------
                SectionLabel("Feed watchdog")
                ToggleRow(
                    // WATCH-10: the dependency on the crying alarm is stated where it is toggled.
                    "Alarm if the feed drops while the crying alarm is active",
                    settings.watchdogEnabled,
                    enabled = alarmOn,
                ) {
                    onChange(settings.copy(watchdogEnabled = it))
                }
                Text(
                    "After ${settings.watchdogGraceSeconds}s without live audio",
                    color = dimmedIf(!watchdogOn, MaterialTheme.colorScheme.onSurface),
                )
                Slider(
                    value = settings.watchdogGraceSeconds.toFloat(),
                    onValueChange = { onChange(settings.copy(watchdogGraceSeconds = it.toInt())) },
                    valueRange = Settings.GRACE_MIN_SECONDS.toFloat()..Settings.GRACE_MAX_SECONDS.toFloat(),
                    steps = 22,
                    enabled = watchdogOn,
                )
                AlarmSoundSettings(
                    sound = settings.feedAlarmSound,
                    volume = settings.feedAlarmVolume,
                    vibrate = settings.feedAlarmVibrate,
                    enabled = watchdogOn,
                    kind = AlarmKind.FEED_DOWN,
                    onSound = { onChange(settings.copy(feedAlarmSound = it)) },
                    onVolume = { onChange(settings.copy(feedAlarmVolume = it)) },
                    onVibrate = { onChange(settings.copy(feedAlarmVibrate = it)) },
                    onPreview = onPreviewSound,
                )

                // --- Picture-in-picture (BG-18/19) --------------------------------
                SectionLabel("Picture-in-picture")
                ToggleRow(
                    "Keep the video floating when you leave the app", // BG-19
                    settings.pipEnabled,
                ) {
                    onChange(settings.copy(pipEnabled = it))
                }
                Text(
                    "When you switch to another app, the live video stays in a small floating window. " +
                        "Audio and the alarm keep working either way.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        },
    )
}

/**
 * ALRM-12: the room level, with the alarm threshold marked on it. Above the threshold the bar
 * turns the alarm colour — so it is obvious at a glance whether this sound would wake you.
 */
@Composable
fun LevelBar(
    level: Float,
    /** Null when no alarm is armed: then there is no line to draw and nothing to be "over". */
    thresholdDb: Float?,
    dimmed: Boolean = false,
    onOverlay: Boolean = false,
    modifier: Modifier = Modifier,
) {
    val max = com.bluzi.babymonitor.monitor.LevelMeter.LEVEL_MAX.toFloat()
    val fraction = (level / max).coerceIn(0f, 1f)
    val thresholdFraction = thresholdDb?.let { (it / max).coerceIn(0f, 1f) }
    val over = thresholdDb != null && level >= thresholdDb

    val trackColor = if (onOverlay) Color(0x55FFFFFF) else MaterialTheme.colorScheme.surfaceVariant
    val quiet = MaterialTheme.colorScheme.primary
    val loud = MaterialTheme.colorScheme.error
    val fillColor = (if (over) loud else quiet).let { if (dimmed) it.copy(alpha = 0.38f) else it }
    val markColor = if (onOverlay) Color.White else MaterialTheme.colorScheme.onSurface

    Box(
        modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp),
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(10.dp)
                .background(trackColor, RoundedCornerShape(5.dp)),
        )
        Box(
            Modifier
                .fillMaxWidth(fraction)
                .height(10.dp)
                .background(fillColor, RoundedCornerShape(5.dp)),
        )
        // The threshold itself: everything to the right of this line alarms.
        if (thresholdFraction != null) {
            Box(
                Modifier
                    .fillMaxWidth(thresholdFraction)
                    .height(10.dp),
                contentAlignment = Alignment.CenterEnd,
            ) {
                Box(
                    Modifier
                        .width(2.dp)
                        .height(16.dp)
                        .background(markColor.copy(alpha = if (dimmed) 0.38f else 0.9f)),
                )
            }
        }
    }
}

/**
 * ALRM-11: one alarm's sound, volume and vibrate, edited as a unit under that alarm's toggle.
 * A preview plays exactly what a real alarm of [kind] would do — this volume, this vibration.
 */
@Composable
private fun AlarmSoundSettings(
    sound: String,
    volume: Double,
    vibrate: Boolean,
    enabled: Boolean,
    kind: AlarmKind,
    onSound: (String) -> Unit,
    onVolume: (Double) -> Unit,
    onVibrate: (Boolean) -> Unit,
    onPreview: (sound: String, volume: Double, vibrate: Boolean, kind: AlarmKind) -> Unit,
) {
    SoundPicker(
        selected = sound,
        enabled = enabled,
        onSelect = onSound,
        onPreview = { onPreview(it, volume, vibrate, kind) },
    )
    Text(
        "Volume: ${"%.0f".format(Locale.ROOT, volume * 100)}%",
        color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurface),
    )
    Slider(
        value = volume.toFloat(),
        onValueChange = { onVolume(it.toDouble()) },
        // Never all the way down: a silent alarm is not an alarm.
        valueRange = Settings.VOLUME_MIN.toFloat()..Settings.VOLUME_MAX.toFloat(),
        steps = 7,
        enabled = enabled,
    )
    Text(
        "Rises from soft to this volume over a few seconds.", // ALRM-14
        style = MaterialTheme.typography.bodySmall,
        color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurfaceVariant),
    )
    ToggleRow("Vibrate as well", vibrate, enabled = enabled, onChange = onVibrate)
}

/**
 * ALRM-11: pick the sound for one alarm. Collapsed (the default) it shows just the current
 * choice; expanding it lists every sound, each previewable — never discover your alarm is
 * inaudible during a real one.
 */
@Composable
private fun SoundPicker(
    selected: String,
    enabled: Boolean,
    onSelect: (String) -> Unit,
    onPreview: (String) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(enabled = enabled) { expanded = !expanded }
            .padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            if (expanded) Icons.Filled.KeyboardArrowDown else Icons.AutoMirrored.Filled.KeyboardArrowRight,
            contentDescription = if (expanded) "Hide sounds" else "Change sound",
            tint = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurfaceVariant),
        )
        Text(
            "Sound: ${alarmSoundLabel(selected)}",
            color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurface),
            modifier = Modifier.padding(start = 4.dp),
        )
    }
    if (expanded) {
        Column(Modifier.selectableGroup()) {
            for (sound in Settings.ALARM_SOUNDS) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clickable(enabled = enabled) { onSelect(sound) }
                        .padding(vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RadioButton(selected = sound == selected, onClick = null, enabled = enabled)
                    Column(Modifier.padding(start = 4.dp).weight(1f)) {
                        Text(
                            alarmSoundLabel(sound),
                            color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurface),
                        )
                        Text(
                            alarmSoundDescription(sound),
                            style = MaterialTheme.typography.bodySmall,
                            color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurfaceVariant),
                        )
                    }
                    IconButton(onClick = { onPreview(sound) }, enabled = enabled) {
                        Icon(Icons.Filled.PlayArrow, contentDescription = "Preview ${alarmSoundLabel(sound)}")
                    }
                }
            }
        }
    }
}

/** Two stacked steppers for the schedule window; each ±30 min, wrapping across midnight. */
@Composable
private fun TimeRangeRow(startMinutes: Int, endMinutes: Int, enabled: Boolean, onChange: (Int, Int) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        TimeStepper("From", startMinutes, enabled) { onChange(it, endMinutes) }
        TimeStepper("To", endMinutes, enabled) { onChange(startMinutes, it) }
    }
}

@Composable
private fun TimeStepper(label: String, minutes: Int, enabled: Boolean, onChange: (Int) -> Unit) {
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurfaceVariant),
            modifier = Modifier.width(48.dp),
        )
        FilledTonalIconButton(
            onClick = { onChange((minutes - 30 + 1440) % 1440) },
            enabled = enabled,
            modifier = Modifier.size(36.dp),
        ) { Icon(Icons.Filled.Remove, contentDescription = "$label: 30 minutes earlier") }
        Text(
            fmtTime(minutes),
            style = MaterialTheme.typography.titleMedium,
            textAlign = TextAlign.Center,
            color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurface),
            modifier = Modifier.width(64.dp),
        )
        FilledTonalIconButton(
            onClick = { onChange((minutes + 30) % 1440) },
            enabled = enabled,
            modifier = Modifier.size(36.dp),
        ) { Icon(Icons.Filled.Add, contentDescription = "$label: 30 minutes later") }
    }
}
