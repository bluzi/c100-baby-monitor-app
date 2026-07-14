package com.bluzi.babymonitor.platform

import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.datetime.Clock
import kotlinx.datetime.TimeZone
import kotlinx.datetime.toLocalDateTime

/** Cryptographically secure random bytes — nonces, NaCl private keys. */
expect fun secureRandomBytes(n: Int): ByteArray

/**
 * Monotonic milliseconds since some fixed point in this process's life.
 *
 * Everything the monitor times — outage length, alarm sustain, cooldown — must be immune to the
 * wall clock jumping (NTP resync, DST). A backwards jump at 3am would otherwise hide an outage
 * and mute the detector. Never use the wall clock for a duration.
 *
 * Each platform supplies the clock that keeps counting under its own kind of suspension: on
 * Android that is elapsed-realtime, which advances through doze. macOS has no equivalent (a
 * sleeping Mac runs nothing at all), so the macOS shell detects sleep explicitly and reports the
 * outage on wake — see DESK-21.
 */
expect fun elapsedRealtimeMs(): Long

/** Blocking I/O (sockets, HTTP). `Dispatchers.IO` is not visible from common code. */
expect val ioDispatcher: CoroutineDispatcher

/** Wall-clock epoch millis. Only for things that are genuinely about the wall clock. */
fun wallClockMs(): Long = Clock.System.now().toEpochMilliseconds()

/**
 * Minutes since local midnight (0..1439). Used only by the alarm schedule (ALRM-7) and the
 * watchdog's arming (WATCH-9), because "only at night" is a statement about the wall clock.
 */
fun wallClockMinutesOfDay(): Int {
    val now = Clock.System.now().toLocalDateTime(TimeZone.currentSystemDefault())
    return now.hour * 60 + now.minute
}
