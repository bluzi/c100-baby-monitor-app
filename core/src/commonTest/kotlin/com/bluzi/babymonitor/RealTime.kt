package com.bluzi.babymonitor

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext

/**
 * For tests that drive genuinely concurrent code — the CS2 transport owns its own scope and its
 * own threads, and a frame only arrives because a worker read it.
 *
 * `runTest` alone will not do here. Its virtual clock advances the moment the test coroutine has
 * nothing left to run, so a `withTimeout(2000)` waiting on a channel that a *real* thread is
 * about to fill fires instantly — the test fails not because the transport is broken but because
 * time skipped past it. Real work needs a real clock.
 */
fun runRealTimeTest(block: suspend CoroutineScope.() -> Unit) = runTest {
    withContext(Dispatchers.Default) { block() }
}
