package com.bluzi.babymonitor.monitor

import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.Test

// Support for LIVE-1/LIVE-7: the camera interleaves config-only access units (VPS/SPS/PPS)
// with keyframes, so parameter sets must be cached across frames (matches c100's fmp4.ts).

private fun nal(type: Int, vararg payload: Int): ByteArray =
    byteArrayOf(0, 0, 0, 1, (type shl 1).toByte(), 1) + payload.map { it.toByte() }.toByteArray()

private fun nal3(type: Int): ByteArray = byteArrayOf(0, 0, 1, (type shl 1).toByte(), 1)

class HevcTest {
    @Test
    fun `parseNals splits 3- and 4-byte start codes and reads types`() {
        val data = nal(32) + nal3(33) + nal(34) + nal(19, 9, 9)
        val types = Hevc.parseNals(data).map { it.type }
        assertEquals(listOf(32, 33, 34, 19), types)
    }

    @Test
    fun `keyframe and VCL detection follow the IRAP ranges`() {
        assertTrue(Hevc.isKeyframe(nal(19))) // IDR_W_RADL
        assertTrue(Hevc.isKeyframe(nal(21))) // CRA
        assertFalse(Hevc.isKeyframe(nal(1))) // trailing picture
        assertTrue(Hevc.hasVclNal(nal(1)))
        assertFalse(Hevc.hasVclNal(nal(32) + nal(33))) // config-only access unit
    }

    @Test
    fun `parameter sets cached across separate access units yield a csd`() {
        val cache = HevcParamCache()
        cache.observe(nal(32, 7)) // VPS alone
        assertNull(cache.csd())
        cache.observe(nal(33, 8) + nal(34, 9)) // SPS+PPS in a later AU
        val csd = cache.csd()!!
        // csd is the three cached NALs, 4-byte start codes, in VPS/SPS/PPS order.
        assertContentEquals(nal(32, 7) + nal(33, 8) + nal(34, 9), csd)
    }

    @Test
    fun `newer parameter sets replace older ones`() {
        val cache = HevcParamCache()
        cache.observe(nal(32, 1) + nal(33, 1) + nal(34, 1))
        cache.observe(nal(33, 2)) // stream re-announces SPS
        val csd = cache.csd()!!
        assertContentEquals(nal(32, 1) + nal(33, 2) + nal(34, 1), csd)
    }
}
