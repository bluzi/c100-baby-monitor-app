package com.bluzi.babymonitor.monitor

// WATCH-2 / WATCH-7: what "the feed is alive" actually means. Pure so the decision the whole
// monitor rests on — is silence real, or is the app lying? — is testable.
//
// The engine reports a status and stamps every decodable *audio* frame — audio is what
// monitoring means; video alone is not a live feed. A connection can be *up* (TCP happy,
// keepalives flowing, video even rendering) while the audio has stopped: camera unplugged,
// a router black-holing traffic, a dead audio stream. That looks identical to a quiet nursery
// unless we check the clock.

/** No audio within this window means the feed is not delivering, whatever the socket thinks. */
const val FEED_STALE_MS = 3_000L

/** A live connection silent this long is dead in all but name: drop it and reconnect (WATCH-7). */
const val STALL_MS = 8_000L

/** Is the feed genuinely delivering audio right now? Anything but a fresh frame is "no". */
fun feedAlive(status: String, lastAudioAtMs: Long, nowMs: Long): Boolean =
    status == STATUS_LIVE && lastAudioAtMs > 0 && nowMs - lastAudioAtMs < FEED_STALE_MS

/**
 * Is the connection claiming to be live while delivering no audio? Such a connection never
 * recovers on its own (the read just blocks), so the engine must force it closed (WATCH-7).
 */
fun feedStalled(status: String, lastAudioAtMs: Long, nowMs: Long): Boolean =
    status == STATUS_LIVE && lastAudioAtMs > 0 && nowMs - lastAudioAtMs > STALL_MS
