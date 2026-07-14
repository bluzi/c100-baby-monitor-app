package com.bluzi.babymonitor.apple

import com.bluzi.babymonitor.monitor.STATUS_LIVE

/**
 * The Mac shell's decisions — the ones that could hide a warning if they were wrong.
 *
 * They are here rather than in a SwiftUI view for the reason every decision in this project is in
 * core: a rule written in a view is a rule nobody can test, and the rule below ("the floating tile
 * may only fade when there is genuinely nothing to see") is one that guards the app's first
 * promise. Get it wrong and a parent glances at a dimmed picture of a sleeping baby while the feed
 * has been dead for an hour.
 *
 * The *look* of the tile is Swift's business. Whether it is allowed to disappear into the
 * background is not.
 */
object MacShell {
    /** MACOS-16: faint enough to see through, never so faint it cannot be seen. */
    const val MINI_OPACITY_MIN = 0.25
    const val MINI_OPACITY_MAX = 1.0
    const val MINI_OPACITY_DEFAULT = 0.55

    /** MACOS-5/14: the two shapes of the one window. */
    const val SHAPE_FULL = "full"
    const val SHAPE_MINI = "mini"

    /**
     * MACOS-16. **Attention is the default.** The tile is allowed to fade only when the monitor is
     * doing exactly what the parent believes it is doing: running, live, no alarm, no expired
     * session, no unread sleep outage. Everything else — including a monitor that is merely
     * *stopped*, which is the quietest failure there is — holds it at full opacity.
     *
     * Written as "healthy or else", never as a list of bad states: a status we forgot to enumerate
     * must fail towards being seen.
     */
    fun needsAttention(health: MonitorHealth): Boolean = !(
        health.running &&
            health.status == STATUS_LIVE &&
            health.activeAlarm == null &&
            !health.sessionExpired &&
            health.sleepOutage == null
        )

    /**
     * MACOS-16 / MACOS-18: how solid the mini window is drawn right now.
     *
     * The clamp is applied here, on the way out, so no caller can forget it and no stored value can
     * produce an invisible monitor.
     */
    fun miniOpacity(
        health: MonitorHealth,
        hovering: Boolean,
        fadeEnabled: Boolean,
        reduceTransparency: Boolean,
        idleOpacity: Double,
    ): Double {
        if (hovering || !fadeEnabled || reduceTransparency) return MINI_OPACITY_MAX
        if (needsAttention(health)) return MINI_OPACITY_MAX
        return clampMiniOpacity(idleOpacity)
    }

    fun clampMiniOpacity(value: Double): Double =
        if (value.isNaN()) MINI_OPACITY_DEFAULT
        else value.coerceIn(MINI_OPACITY_MIN, MINI_OPACITY_MAX)

    /**
     * MACOS-14: which shape the window may take. The mini shape is a *view of a feed* — there is
     * nothing to float before a camera is chosen, and a sign-in form does not belong in a tile the
     * size of a postage stamp. So sign-in and the camera picker force the window full, whatever
     * shape the user last left it in; the shape itself is remembered and comes back with the feed.
     */
    fun windowShape(screen: String, preferred: String): String = when {
        screen != "viewer" -> SHAPE_FULL
        preferred == SHAPE_MINI -> SHAPE_MINI
        else -> SHAPE_FULL
    }
}

/** What the shell needs to know to decide whether the monitor can be allowed to fade quietly. */
data class MonitorHealth(
    val running: Boolean,
    val status: String,
    val activeAlarm: String?,
    val sessionExpired: Boolean,
    val sleepOutage: String?,
)
