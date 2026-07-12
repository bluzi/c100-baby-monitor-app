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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilterChip
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
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.monitor.MonitorHub
import com.bluzi.babymonitor.monitor.alarmSoundDescription
import com.bluzi.babymonitor.monitor.alarmSoundLabel
import com.bluzi.babymonitor.monitor.displayLevelDb
import com.bluzi.babymonitor.monitor.effectiveThresholdDb
import java.util.Locale
import kotlin.math.roundToInt

// Locale.ROOT everywhere: the app is English-only (UI-2) — digits must not localise either.
private fun fmtTime(minutes: Int): String = "%02d:%02d".format(Locale.ROOT, minutes / 60, minutes % 60)

@Composable
private fun SectionLabel(text: String) {
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
        Text(label, color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurface))
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

/**
 * Alert settings: the crying alarm (ALRM-1/2/7/8/11), the feed watchdog (WATCH-1), and how both
 * alarms sound (ALRM-11/14). Every change is pushed through [onChange] immediately so it persists
 * and takes effect right away. Sub-settings stay visible but disabled while their toggle is off,
 * so they can be tuned before being switched on.
 */
@Composable
fun SettingsDialog(
    settings: Settings,
    onChange: (Settings) -> Unit,
    onPreviewSound: (String) -> Unit,
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
                SectionLabel("Crying alarm")
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

                // --- Feed watchdog (WATCH-1/9/10) ---------------------------------
                SectionLabel("Feed watchdog")
                ToggleRow("Alarm if the feed drops", settings.watchdogEnabled, enabled = alarmOn) {
                    onChange(settings.copy(watchdogEnabled = it))
                }
                Text(
                    "Guards the crying alarm: only alarms while the crying alarm is on and " +
                        "within its active hours.", // WATCH-9/10
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    "After ${settings.watchdogGraceSeconds}s without live audio",
                    color = dimmedIf(!alarmOn || !settings.watchdogEnabled, MaterialTheme.colorScheme.onSurface),
                )
                Slider(
                    value = settings.watchdogGraceSeconds.toFloat(),
                    onValueChange = { onChange(settings.copy(watchdogGraceSeconds = it.toInt())) },
                    valueRange = 5f..120f,
                    steps = 22,
                    enabled = alarmOn && settings.watchdogEnabled,
                )

                // --- How the alarms sound (ALRM-11/14) ----------------------------
                SectionLabel("Alarm sounds")
                Text(
                    "The two alarms always sound different, so you know which one woke you.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                SoundPicker(
                    title = "When the baby cries",
                    selected = settings.cryAlarmSound,
                    other = settings.feedAlarmSound,
                    onSelect = { onChange(settings.withSounds(cry = it)) },
                    onPreview = onPreviewSound,
                )
                SoundPicker(
                    title = "When the feed drops",
                    selected = settings.feedAlarmSound,
                    other = settings.cryAlarmSound,
                    onSelect = { onChange(settings.withSounds(feed = it)) },
                    onPreview = onPreviewSound,
                )

                Text("Alarm volume: ${"%.0f".format(Locale.ROOT, settings.alarmVolume * 100)}%")
                Slider(
                    value = settings.alarmVolume.toFloat(),
                    onValueChange = { onChange(settings.copy(alarmVolume = it.toDouble())) },
                    valueRange = 0.2f..1f, // never all the way down: a silent alarm is not an alarm
                    steps = 7,
                )
                Text(
                    "Alarms rise from soft to this volume over a few seconds.", // ALRM-14
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                ToggleRow("Vibrate as well", settings.alarmVibrate) {
                    onChange(settings.copy(alarmVibrate = it))
                }
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

/** ALRM-11: pick a sound for one alarm; the other alarm's sound is shown as unavailable. */
@Composable
private fun SoundPicker(
    title: String,
    selected: String,
    other: String,
    onSelect: (String) -> Unit,
    onPreview: (String) -> Unit,
) {
    Text(title, style = MaterialTheme.typography.bodyMedium)
    Column(Modifier.selectableGroup()) {
        for (sound in Settings.ALARM_SOUNDS) {
            val isOther = sound == other
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable(enabled = !isOther) { onSelect(sound) }
                    .padding(vertical = 2.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                RadioButton(selected = sound == selected, onClick = null, enabled = !isOther)
                Column(Modifier.padding(start = 4.dp).weight(1f)) {
                    Text(
                        alarmSoundLabel(sound),
                        color = dimmedIf(isOther, MaterialTheme.colorScheme.onSurface),
                    )
                    Text(
                        if (isOther) "Used by the other alarm" else alarmSoundDescription(sound),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                // Never discover your alarm is inaudible during a real one — hear it now.
                IconButton(onClick = { onPreview(sound) }) {
                    Icon(Icons.Filled.PlayArrow, contentDescription = "Preview ${alarmSoundLabel(sound)}")
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
        TextButton(onClick = { onChange((minutes - 30 + 1440) % 1440) }, enabled = enabled) { Text("−") }
        Text(
            fmtTime(minutes),
            style = MaterialTheme.typography.titleMedium,
            textAlign = TextAlign.Center,
            color = dimmedIf(!enabled, MaterialTheme.colorScheme.onSurface),
            modifier = Modifier.width(64.dp),
        )
        TextButton(onClick = { onChange((minutes + 30) % 1440) }, enabled = enabled) { Text("+") }
    }
}
