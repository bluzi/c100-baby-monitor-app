package com.bluzi.babymonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.bluzi.babymonitor.xiaomi.NightVisionMode

/** LIVE-10: pick the camera's night-vision mode. Three modes, matching the hardware. */
@Composable
fun NightVisionDialog(
    current: NightVisionMode?,
    error: String?,
    busy: Boolean,
    onSelect: (NightVisionMode) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
        title = { Text("Night vision") },
        text = {
            Column(Modifier.selectableGroup(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                NightVisionOption("Auto", "Camera switches to infrared when it gets dark", NightVisionMode.AUTO, current, busy, onSelect)
                NightVisionOption("On", "Always use infrared night vision", NightVisionMode.ON, current, busy, onSelect)
                NightVisionOption("Off", "Never use night vision", NightVisionMode.OFF, current, busy, onSelect)
                if (current == null) {
                    Text(
                        "Current mode unknown — the camera didn't answer.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                error?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                }
            }
        },
    )
}

@Composable
private fun NightVisionOption(
    label: String,
    description: String,
    mode: NightVisionMode,
    current: NightVisionMode?,
    busy: Boolean,
    onSelect: (NightVisionMode) -> Unit,
) {
    val selected = current == mode
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            // The chosen mode reads as a filled row, not just a dot — obvious at a glance.
            .background(if (selected) MaterialTheme.colorScheme.surfaceVariant else Color.Transparent)
            .selectable(
                selected = selected,
                enabled = !busy, // one write in flight at a time — no revert races
                role = Role.RadioButton,
                onClick = { onSelect(mode) },
            )
            .padding(horizontal = 6.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = null, enabled = !busy)
        Column(Modifier.padding(start = 4.dp, top = 4.dp, bottom = 4.dp)) {
            Text(label, style = MaterialTheme.typography.bodyLarge)
            Text(
                description,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}
