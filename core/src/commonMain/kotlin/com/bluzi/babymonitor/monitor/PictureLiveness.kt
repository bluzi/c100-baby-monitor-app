package com.bluzi.babymonitor.monitor

import kotlin.concurrent.Volatile

/**
 * WATCH-12: is the picture still moving?
 *
 * The mirror of [StreamWatchdog]. That one guards audio dying while video flows; this one guards
 * video dying while audio flows — which is worse, because nothing about it looks wrong. The feed is
 * live, the status says so truthfully, the crying alarm is listening, and the parent is looking at a
 * photograph of a quiet cot.
 *
 * "Moving" is judged on **what arrived**, not on what was drawn: a decoder is a shell's business and
 * a picture that stopped arriving, a picture whose timeline stopped, and a camera repeating one frame
 * are one failure to a parent. Each shows up here as "nothing changed", and none of them needs a
 * decoded pixel to notice.
 *
 * Deliberately pure — no clock, no sockets, `nowMs` passed in — so the same decision runs on the JVM,
 * on Kotlin/Native and in the C# port's suite, and so it can be tested without a camera.
 */
class PictureLiveness(
    /** How long a still picture is allowed to stand before it is called frozen. */
    var freezeMs: Long = FREEZE_MS_DEFAULT,
) {
    // Written by the coroutine reading frames off the camera, read by the watchdog's tick loop — two
    // threads, like MonitorHub.lastAudioAtMs next door. Unsynchronised, the tick could read a stale
    // change time and call a working feed frozen (a reconnect, and a gap in the sound, for nothing) or
    // miss a real freeze; a plain Long can be read torn as well.
    @Volatile
    private var lastChangeMs = 0L

    @Volatile
    private var lastSignature = 0

    @Volatile
    private var seenPicture = false

    /**
     * A video frame arrived. [annexB] is the encoded frame as it came off the wire — its bytes are the
     * cheapest honest answer to "is this a different picture?", because the camera burns a clock into
     * the image, so no two seconds of a working feed encode alike.
     *
     * The frame's timestamp is deliberately **not** consulted. It advances on every frame of a live
     * stream whether or not the picture behind it is moving, so believing it would mean a repeated
     * frame — the failure that matters most here, because it is the one that still looks like a busy
     * socket and a live feed — could never be seen at all. Only the picture speaks for the picture.
     */
    fun onFrame(annexB: ByteArray, nowMs: Long) {
        val signature = signatureOf(annexB)
        val changed = !seenPicture || signature != lastSignature
        seenPicture = true
        lastSignature = signature
        if (changed) lastChangeMs = nowMs
    }

    /** A new session, or a picture that has just started: nothing is stale yet. */
    fun reset() {
        lastChangeMs = 0L
        lastSignature = 0
        seenPicture = false
    }

    /**
     * Has the picture stood still too long?
     *
     * False until a picture has actually been seen: a camera that never sends video is a capability
     * gap that has already been said out loud (LIVE-7, DESK-22), not a freeze, and reconnecting for
     * ever over a picture that was never coming would take the sound down with it.
     */
    fun frozen(nowMs: Long): Boolean = seenPicture && nowMs - lastChangeMs > freezeMs

    private fun signatureOf(annexB: ByteArray): Int {
        // FNV-1a over the length and a bounded sample of the bytes. A full hash of a 2304x1296
        // keyframe, 25 times a second, would be real work for a decision that only needs "did
        // anything at all change" — and an encoder cannot hold length, head, middle and tail
        // identical across a frame that differs.
        var h = -0x7ee3623b // FNV offset basis
        fun mix(b: Int) {
            h = (h xor b) * 16777619
        }
        mix(annexB.size)
        if (annexB.isEmpty()) return h
        val step = if (annexB.size <= SAMPLE_BYTES) 1 else annexB.size / SAMPLE_BYTES
        var i = 0
        while (i < annexB.size) {
            mix(annexB[i].toInt() and 0xff)
            i += step
        }
        mix(annexB[annexB.size - 1].toInt() and 0xff)
        return h
    }

    companion object {
        /**
         * Long enough that a working feed can never look frozen — the picture carries a clock, so it
         * changes every second — and short enough that nobody trusts a photograph for long.
         */
        const val FREEZE_MS_DEFAULT = 10_000L

        private const val SAMPLE_BYTES = 64
    }
}
