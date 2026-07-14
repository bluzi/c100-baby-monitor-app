package com.bluzi.babymonitor.platform

import android.os.SystemClock
import java.security.SecureRandom
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers

private val secureRandom = SecureRandom()

actual fun secureRandomBytes(n: Int): ByteArray = ByteArray(n).also { secureRandom.nextBytes(it) }

/** Advances through doze — unlike System.nanoTime(), which would hide an overnight outage. */
actual fun elapsedRealtimeMs(): Long = SystemClock.elapsedRealtime()

actual val ioDispatcher: CoroutineDispatcher = Dispatchers.IO
