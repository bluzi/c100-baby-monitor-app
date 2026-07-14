package com.bluzi.babymonitor.platform

import kotlin.time.TimeSource
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.convert
import kotlinx.cinterop.usePinned
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.DelicateCoroutinesApi
import kotlinx.coroutines.newFixedThreadPoolContext
import platform.posix.arc4random_buf

@OptIn(ExperimentalForeignApi::class)
actual fun secureRandomBytes(n: Int): ByteArray {
    if (n == 0) return ByteArray(0)
    val out = ByteArray(n)
    out.usePinned { arc4random_buf(it.addressOf(0), n.convert()) }
    return out
}

private val processStart = TimeSource.Monotonic.markNow()

/**
 * Monotonic, but it does NOT advance while the Mac is asleep — nothing can, because the process
 * is not running. That is a real gap in what a Mac can promise, and it is not papered over here:
 * the macOS shell watches for sleep/wake and reports the outage (DESK-21).
 */
actual fun elapsedRealtimeMs(): Long = processStart.elapsedNow().inWholeMilliseconds

/**
 * Kotlin/Native has no public `Dispatchers.IO`, and the monitor's socket reads block: a CS2 read
 * parks its thread until a frame arrives or the read times out. Running those on Dispatchers.Default
 * (one thread per core) would let a couple of stalled reads starve everything else — including the
 * watchdog tick that is supposed to notice the stall. So: a dedicated pool, sized for the handful
 * of blocking reads the monitor can have in flight at once, and alive for the whole process.
 */
@OptIn(DelicateCoroutinesApi::class)
actual val ioDispatcher: CoroutineDispatcher = newFixedThreadPoolContext(16, "baby-monitor-io")
