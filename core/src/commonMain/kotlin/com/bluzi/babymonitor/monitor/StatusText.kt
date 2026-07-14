package com.bluzi.babymonitor.monitor

// LIVE-4 / APP-3 / BG-8: the engine publishes terse machine statuses; these helpers keep what the
// user sees (status line and notification) readable — and keep a dead session from masquerading
// as a connection blip that will fix itself.

const val STATUS_IDLE = "idle"
const val STATUS_CONNECTING = "connecting"
const val STATUS_LIVE = "live"
const val STATUS_STOPPED = "stopped"
const val STATUS_SESSION_EXPIRED = "session-expired"
const val STATUS_MONITOR_FAILED = "monitor-failed" // WATCH-11
const val STATUS_UNSUPPORTED_CAMERA = "unsupported-camera" // LIVE-12

/** Status while waiting to reconnect — whole seconds, rounded up, counting down (LIVE-4). */
fun reconnectStatus(remainingMs: Long): String =
    "reconnecting in ${(remainingMs + 999) / 1000}s"

/**
 * LIVE-2/4: the live feed's one-line summary. Muted is stated in words — at a glance, and
 * without having to know which way the speaker icon's convention goes.
 */
fun statusLine(cameraName: String, rawStatus: String, muted: Boolean): String =
    "${cameraName.ifEmpty { "Camera" }} — ${friendlyStatus(rawStatus)}${if (muted) " · muted" else ""}"

/** Map a raw engine status to user-facing copy (APP-3). Unknown statuses pass through. */
fun friendlyStatus(raw: String): String = when {
    raw == STATUS_IDLE -> "Starting…"
    raw == STATUS_CONNECTING -> "Connecting…"
    raw == STATUS_LIVE -> "Live"
    raw == STATUS_STOPPED -> "Stopped"
    raw == STATUS_SESSION_EXPIRED -> "Session expired — open the app to sign in" // BG-8
    raw == STATUS_MONITOR_FAILED -> "Monitoring stopped working — press Resume to restart" // WATCH-11
    raw == STATUS_UNSUPPORTED_CAMERA -> "This camera model isn't supported" // LIVE-12
    raw.startsWith("reconnecting") -> raw.replaceFirstChar { it.uppercase() }
    raw.startsWith("error:") -> "Connection lost — retrying"
    else -> raw
}
