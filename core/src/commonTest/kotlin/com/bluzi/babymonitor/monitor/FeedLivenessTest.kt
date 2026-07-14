package com.bluzi.babymonitor.monitor

import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.test.Test

// The wiring between "what the engine reports" and "is the feed actually alive" — the decision
// the watchdog (WATCH-2) and the stall-reconnect (WATCH-7) hang on.

class FeedLivenessTest {
    @Test
    fun `WATCH-2 the feed is alive only while live and audio is still arriving`() {
        val now = 100_000L
        assertTrue(feedAlive("live", lastAudioAtMs = now - 500, nowMs = now))
        assertFalse(feedAlive("live", lastAudioAtMs = now - 10_000, nowMs = now)) // gone quiet
        assertFalse(feedAlive("connecting", lastAudioAtMs = now, nowMs = now))
        assertFalse(feedAlive("reconnecting in 5s", lastAudioAtMs = now, nowMs = now))
        assertFalse(feedAlive("error: boom", lastAudioAtMs = now, nowMs = now))
        assertFalse(feedAlive("stopped", lastAudioAtMs = now, nowMs = now))
        assertFalse(feedAlive("live", lastAudioAtMs = 0, nowMs = now)) // no audio ever
    }

    @Test
    fun `WATCH-7 a live connection that stops delivering audio is stalled and must be dropped`() {
        val now = 100_000L
        assertFalse(feedStalled("live", lastAudioAtMs = now - 1_000, nowMs = now))
        assertTrue(feedStalled("live", lastAudioAtMs = now - STALL_MS - 1, nowMs = now))
        // Only a *live* connection can stall; reconnect states are already handled by the loop.
        assertFalse(feedStalled("connecting", lastAudioAtMs = 0, nowMs = now))
        assertFalse(feedStalled("reconnecting in 5s", lastAudioAtMs = 0, nowMs = now))
    }

    @Test
    fun `WATCH-7 a stalled feed is never reported as alive`() {
        val now = 100_000L
        val stalled = feedStalled("live", lastAudioAtMs = now - STALL_MS - 1, nowMs = now)
        val alive = feedAlive("live", lastAudioAtMs = now - STALL_MS - 1, nowMs = now)
        assertTrue(stalled)
        assertFalse(alive)
    }
}
