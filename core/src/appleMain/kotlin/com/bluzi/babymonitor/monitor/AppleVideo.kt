package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.net.toNSData
import kotlin.concurrent.Volatile
import kotlinx.cinterop.ExperimentalForeignApi
import platform.Foundation.NSData

private const val TAG = "video"

/**
 * What the Mac's window plugs in to draw the picture. Implemented in Swift, because VideoToolbox
 * and AVSampleBufferDisplayLayer are far pleasanter from there — and because video is best-effort
 * (LIVE-7), so a bridge in this path costs nothing that matters. The audio path has no bridge in
 * it, and that is the deliberate asymmetry.
 *
 * The bitstream work stays in Kotlin: the camera sends VPS/SPS/PPS in their own access units,
 * separate from keyframes, so they must be cached across frames and the decoder configured at the
 * next keyframe. Every platform's decoder needs exactly that, so it is not re-derived in Swift.
 */
interface VideoRenderer {
    /** Configure (or reconfigure) the decoder. Called before any frame, and again on a new stream. */
    fun configure(vps: NSData, sps: NSData, pps: NSData)

    /** One Annex-B access unit. Must not throw — see [AppleVideoOutput.push]. */
    fun decode(annexB: NSData, ptsMs: Long)

    /** Not called `release`: that collides with NSObject's own, and Swift cannot then conform. */
    fun tearDown()
}

/** Where the picture goes, when there is anywhere for it to go. Set by whichever window is showing. */
object AppleVideo {
    @Volatile
    var renderer: VideoRenderer? = null
}

@OptIn(ExperimentalForeignApi::class)
class AppleVideoOutput : VideoOutput {
    private val params = HevcParamCache()
    private var configuredFor: VideoRenderer? = null
    private var failures = 0
    private var loggedWaiting = false

    /**
     * LIVE-7: never throws. Video trouble must never take audio monitoring down with it — a black
     * picture is a disappointment, a dead alarm is the thing this project exists to prevent.
     */
    override fun push(annexB: ByteArray, ptsMs: Long) {
        try {
            params.observe(annexB)
            if (!Hevc.hasVclNal(annexB)) return // config-only access unit

            val renderer = AppleVideo.renderer
            if (renderer == null) {
                // No window is showing. Audio is unaffected (LIVE-7), and the next window to open
                // reconfigures at the next keyframe.
                if (configuredFor != null) {
                    configuredFor = null
                    loggedWaiting = false
                }
                return
            }
            if (failures >= 8) return // this session's video is a lost cause; audio continues

            if (configuredFor !== renderer) {
                if (!Hevc.isKeyframe(annexB)) return // wait for a clean entry point
                val sets = params.parameterSets()
                if (sets == null) {
                    if (!loggedWaiting) {
                        loggedWaiting = true
                        Log.i(TAG, "keyframe seen but VPS/SPS/PPS not yet cached — waiting")
                    }
                    return
                }
                renderer.configure(sets[0].toNSData(), sets[1].toNSData(), sets[2].toNSData())
                configuredFor = renderer
                Log.i(TAG, "H265 decoder configured (vps ${sets[0].size}B sps ${sets[1].size}B pps ${sets[2].size}B)")
            }

            renderer.decode(annexB.toNSData(), ptsMs)
        } catch (e: Throwable) {
            failures++
            Log.w(TAG, "decode failed (attempt $failures/8), retrying at next keyframe", e)
            configuredFor = null
        }
    }

    override fun release() {
        runCatching { AppleVideo.renderer?.tearDown() }
        configuredFor = null
    }
}

/** The Mac's media stack, handed to the engine. */
object AppleMedia : MediaFactory {
    override fun audio(onPcmWindow: (pcm: ShortArray, sampleRate: Int) -> Unit): AudioOutput =
        AppleAudioOutput(onPcmWindow = onPcmWindow)

    override fun video(): VideoOutput = AppleVideoOutput()
}
