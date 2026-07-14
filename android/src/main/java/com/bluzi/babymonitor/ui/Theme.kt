package com.bluzi.babymonitor.ui

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

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
    // Container roles: dialogs, cards, chips and tonal buttons pick these up. Without them M3
    // falls back to purple-tinted neutrals that sit outside the blue-grey night family.
    surfaceContainerLowest = Color(0xFF0A0D10),
    surfaceContainerLow = Color(0xFF12161B),
    surfaceContainer = Color(0xFF171C22),
    surfaceContainerHigh = Color(0xFF1D232A),
    surfaceContainerHighest = Color(0xFF232A32),
    primaryContainer = Color(0xFF2A4258),
    onPrimaryContainer = Color(0xFFD3E4F5),
    secondaryContainer = Color(0xFF283542),
    onSecondaryContainer = Color(0xFFD3E4F5),
    errorContainer = Color(0xFF5C2E26),
    onErrorContainer = Color(0xFFFFDAD2),
    outline = Color(0xFF3A434D),
    outlineVariant = Color(0xFF262E36),
)

// Softer corners everywhere: text fields and menus (extraSmall), chips (small), cards (medium).
private val NightShapes = Shapes(
    extraSmall = RoundedCornerShape(10.dp),
    small = RoundedCornerShape(10.dp),
    medium = RoundedCornerShape(16.dp),
    large = RoundedCornerShape(22.dp),
)

@Composable
fun BabyMonitorTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = NightColors, shapes = NightShapes, content = content)
}
