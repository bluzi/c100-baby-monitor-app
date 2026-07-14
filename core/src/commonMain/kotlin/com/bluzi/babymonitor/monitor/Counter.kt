package com.bluzi.babymonitor.monitor

import kotlin.concurrent.atomics.AtomicInt
import kotlin.concurrent.atomics.ExperimentalAtomicApi

/**
 * An atomic counter. The producer (the frame reader) and the consumers (the decode loops) run on
 * different threads, so these counts must not tear — a lost decrement would leave the video
 * catch-up permanently convinced it was behind, and it would stop feeding the decoder for good.
 *
 * Wrapped rather than used directly so the experimental opt-in lives in exactly one place, and
 * built from load + compareAndSet alone so it does not depend on the shape of an API still in
 * preview.
 */
@OptIn(ExperimentalAtomicApi::class)
class Counter(initial: Int = 0) {
    private val value = AtomicInt(initial)

    fun increment(): Int = add(1)

    fun decrement(): Int = add(-1)

    fun get(): Int = value.load()

    private fun add(delta: Int): Int {
        while (true) {
            val current = value.load()
            val next = current + delta
            if (value.compareAndSet(current, next)) return next
        }
    }
}
