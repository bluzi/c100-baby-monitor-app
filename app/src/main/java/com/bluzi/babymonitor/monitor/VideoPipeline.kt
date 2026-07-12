package com.bluzi.babymonitor.monitor

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import com.bluzi.babymonitor.log.Log
import java.nio.ByteBuffer

// H.265 Annex-B → MediaCodec → Surface. Best-effort by design: every failure here is caught
// and retried at the next keyframe so audio monitoring never depends on video (LIVE-7).
//
// Stream shape (learned from c100's fmp4.ts, the working implementation): the camera sends
// VPS/SPS/PPS in their own "config-only" access units, separate from keyframes — so parameter
// sets must be cached across frames and the decoder configured at the next keyframe.

object Hevc {
    const val NAL_VPS = 32
    const val NAL_SPS = 33
    const val NAL_PPS = 34

    data class Nal(val type: Int, val start: Int, val end: Int) // payload range [start, end)

    /** Split an Annex-B buffer (3- or 4-byte start codes) into NAL payload ranges. */
    fun parseNals(data: ByteArray): List<Nal> {
        val nals = mutableListOf<Nal>()
        var payloadStart = -1
        fun closeAt(end: Int) {
            if (payloadStart in 0 until end) {
                nals.add(Nal((data[payloadStart].toInt() shr 1) and 0x3f, payloadStart, end))
            }
        }
        var i = 0
        while (i + 2 < data.size) {
            if (data[i] == 0.toByte() && data[i + 1] == 0.toByte()) {
                val three = data[i + 2] == 1.toByte()
                val four = !three && data[i + 2] == 0.toByte() && i + 3 < data.size && data[i + 3] == 1.toByte()
                if (three || four) {
                    closeAt(i)
                    payloadStart = i + if (three) 3 else 4
                    i = payloadStart
                    continue
                }
            }
            i++
        }
        closeAt(data.size)
        return nals
    }

    /** VCL NAL types are 0..31; IRAP (BLA/IDR/CRA, 16..23) can start decoding. */
    fun isVcl(type: Int): Boolean = type < 32
    fun isIrap(type: Int): Boolean = type in 16..23

    fun hasVclNal(data: ByteArray): Boolean = parseNals(data).any { isVcl(it.type) }
    fun isKeyframe(data: ByteArray): Boolean = parseNals(data).any { isIrap(it.type) }
}

/**
 * Caches the stream's parameter sets as they fly by (they arrive in separate access units)
 * and yields an Annex-B csd-0 blob once all three have been seen. Pure — unit-tested.
 */
class HevcParamCache {
    private val sets = mutableMapOf<Int, ByteArray>()

    fun observe(data: ByteArray) {
        for (nal in Hevc.parseNals(data)) {
            if (nal.type == Hevc.NAL_VPS || nal.type == Hevc.NAL_SPS || nal.type == Hevc.NAL_PPS) {
                sets[nal.type] = data.copyOfRange(nal.start, nal.end)
            }
        }
    }

    val complete: Boolean
        get() = sets.containsKey(Hevc.NAL_VPS) && sets.containsKey(Hevc.NAL_SPS) && sets.containsKey(Hevc.NAL_PPS)

    /** 00 00 00 01 VPS ‖ 00 00 00 01 SPS ‖ 00 00 00 01 PPS, or null until complete. */
    fun csd(): ByteArray? {
        if (!complete) return null
        val startCode = byteArrayOf(0, 0, 0, 1)
        var out = ByteArray(0)
        for (type in intArrayOf(Hevc.NAL_VPS, Hevc.NAL_SPS, Hevc.NAL_PPS)) {
            out = out + startCode + sets.getValue(type)
        }
        return out
    }
}

private const val TAG = "video"

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
                    Log.i(TAG, "video: surface gone, releasing decoder (audio unaffected)")
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
                        Log.i(TAG, "video: keyframe seen but VPS/SPS/PPS not yet cached — waiting")
                    }
                    return
                }
                release()
                configure(csd, surface)
                Log.i(TAG, "video: H265 decoder configured (csd ${csd.size}B)")
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
            Log.w(TAG, "video: decode failed (attempt $failures/8), retrying at next keyframe", e)
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
