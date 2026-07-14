package com.bluzi.babymonitor.ui

import com.bluzi.babymonitor.monitor.STATUS_MONITOR_FAILED

/**
 * Which controls the live feed offers, and when.
 *
 * Shared, so that a button row on a phone and a menu in a Mac's menu bar cannot come to different
 * conclusions about whether monitoring can be stopped or resumed. The *look* of a control is each
 * platform's business; whether it exists is the spec's (BG-11, WATCH-11).
 */
enum class ViewerActionKind { Resume, Stop, Mute, NightVision, Alerts }

fun viewerActionKinds(running: Boolean, status: String): List<ViewerActionKind> = buildList {
    // APP-3/WATCH-11: a failed monitor keeps `running` true (the watchdog still guards), so it
    // needs its own Resume — the failure must be recoverable right here, not by reopening the app.
    if (!running || status == STATUS_MONITOR_FAILED) add(ViewerActionKind.Resume)
    // BG-11: a running monitor can always be stopped from the feed itself — the notification (or
    // the tray) must not be the only way. A failed monitor is still running, so it offers both.
    if (running) add(ViewerActionKind.Stop)
    add(ViewerActionKind.Mute)
    add(ViewerActionKind.NightVision)
    add(ViewerActionKind.Alerts)
}
