package com.bluzi.babymonitor.monitor

import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.test.Test

class CatchupTest {
    @Test
    fun `LIVE-8 frames flow normally while the consumer keeps up`() {
        val catchup = VideoCatchup(maxBacklogFrames = 30)
        repeat(100) { i ->
            assertTrue(catchup.admit(isKeyframe = i % 40 == 0, backlogFrames = 2))
        }
    }

    @Test
    fun `LIVE-8 a backlog drops frames until the next keyframe then resumes`() {
        val catchup = VideoCatchup(maxBacklogFrames = 30)
        assertTrue(catchup.admit(isKeyframe = true, backlogFrames = 0))

        // Decoder stalls; backlog grows past the limit → non-key frames are dropped…
        assertFalse(catchup.admit(isKeyframe = false, backlogFrames = 45))
        assertFalse(catchup.admit(isKeyframe = false, backlogFrames = 44))
        // …even after the backlog drains, until a clean entry point arrives…
        assertFalse(catchup.admit(isKeyframe = false, backlogFrames = 3))
        // …the next keyframe re-enters and playback resumes.
        assertTrue(catchup.admit(isKeyframe = true, backlogFrames = 2))
        assertTrue(catchup.admit(isKeyframe = false, backlogFrames = 1))
    }

    @Test
    fun `LIVE-8 a keyframe that itself trips the limit is still shown`() {
        val catchup = VideoCatchup(maxBacklogFrames = 30)
        assertTrue(catchup.admit(isKeyframe = true, backlogFrames = 50))
        assertTrue(catchup.admit(isKeyframe = false, backlogFrames = 0))
    }
}
