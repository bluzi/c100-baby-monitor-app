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
import com.bluzi.babymonitor.data.Settings

/**
 * LIVE-18: pick the picture this viewer asks the camera for. Two choices, and no error path —
 * unlike night vision (LIVE-10) this is a local settings write, not a write to the camera, so
 * there is nothing to fail, nothing to be busy for, and nothing to revert.
 */
@Composable
fun PictureQualityDialog(
    current: String,
    /** LIVE-18: a stopped monitor has no feed to interrupt — don't promise an interruption. */
    running: Boolean,
    onSelect: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
        title = { Text("Picture quality") },
        text = {
            Column(Modifier.selectableGroup(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                PictureQualityOption("HD", "The camera's full picture", Settings.QUALITY_HD, current, onSelect)
                PictureQualityOption("SD", "Smaller — for a network that can't carry HD", Settings.QUALITY_SD, current, onSelect)
                if (running) {
                    // LIVE-18: the quality is chosen when the stream is asked for, so changing it
                    // reconnects. Said here rather than discovered — a parent who chose the pause
                    // is not surprised by it.
                    Text(
                        "Changing this reconnects the feed — sound and picture stop for a second or two.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
    )
}

@Composable
private fun PictureQualityOption(
    label: String,
    description: String,
    quality: String,
    current: String,
    onSelect: (String) -> Unit,
) {
    val selected = current == quality
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            // The chosen quality reads as a filled row, not just a dot — obvious at a glance.
            .background(if (selected) MaterialTheme.colorScheme.surfaceVariant else Color.Transparent)
            .selectable(
                selected = selected,
                role = Role.RadioButton,
                onClick = { onSelect(quality) },
            )
            .padding(horizontal = 6.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = null)
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
