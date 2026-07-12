package com.bluzi.babymonitor.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// UI-1: always dark — this app is used in a dark room at night.
private val NightColors = darkColorScheme(
    primary = Color(0xFF7FB4E6),
    onPrimary = Color(0xFF0B1826),
    secondary = Color(0xFFE6C97F),
    background = Color(0xFF0C0F12),
    onBackground = Color(0xFFD9DEE3),
    surface = Color(0xFF14181D),
    onSurface = Color(0xFFD9DEE3),
    surfaceVariant = Color(0xFF1C2127),
    onSurfaceVariant = Color(0xFF9AA3AC),
    error = Color(0xFFE68F7F),
)

@Composable
fun BabyMonitorTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = NightColors, content = content)
}
