package com.bluzi.babymonitor.monitor

// H.265 bitstream shape, shared by every platform's decoder.
//
// Stream shape (learned from c100's fmp4.ts, the working implementation): the camera sends
// VPS/SPS/PPS in their own "config-only" access units, separate from keyframes — so parameter
// sets must be cached across frames and the decoder configured at the next keyframe. Both
// MediaCodec (Android) and VideoToolbox (Apple) need exactly this, so it lives here rather than
// being re-derived per platform.

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
 * Caches the stream's parameter sets as they fly by (they arrive in separate access units) and
 * yields them once all three have been seen. Pure — unit-tested.
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

    /** The raw VPS, SPS and PPS payloads (no start codes) — what VideoToolbox wants. */
    fun parameterSets(): List<ByteArray>? {
        if (!complete) return null
        return listOf(Hevc.NAL_VPS, Hevc.NAL_SPS, Hevc.NAL_PPS).map { sets.getValue(it) }
    }

    /** 00 00 00 01 VPS ‖ 00 00 00 01 SPS ‖ 00 00 00 01 PPS, or null until complete — MediaCodec's csd-0. */
    fun csd(): ByteArray? {
        val params = parameterSets() ?: return null
        val startCode = byteArrayOf(0, 0, 0, 1)
        var out = ByteArray(0)
        for (set in params) out = out + startCode + set
        return out
    }
}
