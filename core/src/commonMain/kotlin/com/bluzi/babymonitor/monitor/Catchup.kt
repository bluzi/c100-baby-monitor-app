package com.bluzi.babymonitor.monitor

// LIVE-8: live video must never fall progressively behind. When the consumer can't keep up
// (slow decoder, render stall), we stop feeding frames and resume at the next keyframe —
// decoding a partial GOP would only show artifacts.

const val AUDIO_MAX_BACKLOG_PACKETS = 25 // ≈1 s of 40 ms Opus packets; older ones get dropped

class VideoCatchup(private val maxBacklogFrames: Int = 30) {
    private var skipping = false

    /**
     * Decide whether to feed this frame to the decoder. [backlogFrames] is how many frames
     * are still queued behind it. Once the backlog exceeds the limit, frames are dropped
     * until the next keyframe, which re-enters cleanly.
     */
    fun admit(isKeyframe: Boolean, backlogFrames: Int): Boolean {
        if (backlogFrames > maxBacklogFrames) skipping = true
        if (!skipping) return true
        if (!isKeyframe) return false
        skipping = false
        return true
    }
}
