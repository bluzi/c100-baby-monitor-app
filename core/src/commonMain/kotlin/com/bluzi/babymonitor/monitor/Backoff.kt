package com.bluzi.babymonitor.monitor

// LIVE-5: reconnect waits grow from sub-second to a capped tens-of-seconds.
val RECONNECT_BACKOFF_MS = listOf(500L, 1000L, 2000L, 5000L, 10_000L, 15_000L)

fun reconnectDelayMs(attempt: Int): Long =
    RECONNECT_BACKOFF_MS[attempt.coerceIn(0, RECONNECT_BACKOFF_MS.size - 1)]
