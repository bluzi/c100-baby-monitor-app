package com.bluzi.babymonitor.monitor

import kotlin.math.abs
import kotlin.math.roundToLong

/**
 * One decimal place, locale-independent. `String.format` is JVM-only, and the room-level log line
 * (LIVE-6) is read off a device to reconstruct a night — it must look the same everywhere, and a
 * locale that writes "0,0" would make it un-greppable.
 */
fun oneDecimal(value: Double): String {
    if (value.isNaN()) return "NaN"
    if (value.isInfinite()) return if (value > 0) "Inf" else "-Inf"
    val scaled = (abs(value) * 10).roundToLong()
    val sign = if (value < 0 && scaled != 0L) "-" else ""
    return "$sign${scaled / 10}.${scaled % 10}"
}
