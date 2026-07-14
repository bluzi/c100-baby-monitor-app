package com.bluzi.babymonitor.monitor

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import com.bluzi.babymonitor.log.Log
import java.nio.ByteBuffer

// Android's VideoOutput: H.265 Annex-B → MediaCodec → Surface. Best-effort by design: every
// failure here is caught and retried at the next keyframe so audio monitoring never depends on
// video (LIVE-7). The bitstream parsing itself (Hevc, HevcParamCache) lives in core — every
// platform's decoder needs the same parameter-set caching, so it is not re-derived here.

private const val TAG = "video"

/**
 * Where the picture goes, when there is anywhere for it to go. Held here rather than in
 * MonitorHub because a Surface is Android's business alone: the engine has no concept of one,
 * and audio never depends on it (LIVE-7).
 */
object VideoSurface {
    @Volatile
    var surface: Surface? = null
}

class AndroidVideoOutput : VideoOutput {
    private val renderer = HevcRenderer()

    override fun push(annexB: ByteArray, ptsMs: Long) = renderer.push(annexB, ptsMs, VideoSurface.surface)

    override fun release() = renderer.release()
}

class HevcRenderer {
    private var codec: MediaCodec? = null
    private var configuredSurface: Surface? = null
    private val params = HevcParamCache()
    private val info = MediaCodec.BufferInfo()
    private var failures = 0
    private var loggedWaiting = false

    /**
     * Feed one access unit. Parameter sets are cached from every AU; the decoder configures
     * (or reconfigures after a surface change / error) at the next keyframe. Never throws.
     */
    fun push(data: ByteArray, ptsMs: Long, surface: Surface?) {
        try {
            params.observe(data)
            if (!Hevc.hasVclNal(data)) return // config-only access unit

            if (surface == null || !surface.isValid) {
                if (codec != null) {
                    Log.i(TAG, "surface gone, releasing decoder (audio unaffected)")
                    release()
                }
                return
            }
            if (failures >= 8) return // this session's video is a lost cause; audio continues

            if (codec == null || configuredSurface !== surface) {
                if (!Hevc.isKeyframe(data)) return // wait for a clean entry point
                val csd = params.csd()
                if (csd == null) {
                    if (!loggedWaiting) {
                        loggedWaiting = true
                        Log.i(TAG, "keyframe seen but VPS/SPS/PPS not yet cached — waiting")
                    }
                    return
                }
                release()
                configure(csd, surface)
                Log.i(TAG, "H265 decoder configured (csd ${csd.size}B)")
            }

            val c = codec ?: return
            val inIdx = c.dequeueInputBuffer(20_000)
            if (inIdx >= 0) {
                val buf = c.getInputBuffer(inIdx)!!
                buf.clear()
                buf.put(data)
                c.queueInputBuffer(inIdx, 0, data.size, ptsMs * 1000, 0)
            }
            while (true) {
                val outIdx = c.dequeueOutputBuffer(info, 0)
                if (outIdx < 0) return
                c.releaseOutputBuffer(outIdx, true) // render as fast as it arrives — live view
            }
        } catch (e: Exception) {
            failures++
            Log.w(TAG, "decode failed (attempt $failures/8), retrying at next keyframe", e)
            release()
        }
    }

    private fun configure(csd: ByteArray, surface: Surface) {
        // Nominal size; the decoder reads the real dimensions from the in-band SPS.
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_HEVC, 1920, 1080)
        format.setByteBuffer("csd-0", ByteBuffer.wrap(csd))
        // Low-latency hint (API 30+): some decoders buffer several frames without it.
        if (android.os.Build.VERSION.SDK_INT >= 30) {
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        }
        val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_HEVC)
        codec = c // publish before configure/start: release() can only free what it can see
        c.configure(format, surface, null, 0)
        c.start()
        configuredSurface = surface
    }

    /** Frees the decoder whatever state it is in — a stop() that throws must not skip release(). */
    fun release() {
        val c = codec
        codec = null
        configuredSurface = null
        if (c != null) {
            try { c.stop() } catch (_: Exception) {}
            try { c.release() } catch (_: Exception) {}
        }
    }
}
