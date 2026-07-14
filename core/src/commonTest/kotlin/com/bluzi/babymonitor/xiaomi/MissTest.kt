package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.json.JSONObject
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.Test

class MissTest {
    @Test
    fun `PROTO-20 auth payload carries public key — sign — empty uuid — no encryption`() {
        val json = JSONObject(Miss.authPayload("aabbcc", "SIGNTOKEN"))
        assertEquals("aabbcc", json.getString("public_key"))
        assertEquals("SIGNTOKEN", json.getString("sign"))
        assertEquals("", json.getString("uuid"))
        assertEquals(0, json.getInt("support_encrypt"))
    }

    @Test
    fun `PROTO-21 quality maps hd to 2 for C100 and 3 for C200-C300`() {
        assertEquals("""{"videoquality":2,"enableaudio":1}""", Miss.startMediaBody("chuangmi.camera.077ac1", "hd", true))
        assertEquals("""{"videoquality":3,"enableaudio":1}""", Miss.startMediaBody(Miss.MODEL_C200, "hd", true))
        assertEquals("""{"videoquality":3,"enableaudio":1}""", Miss.startMediaBody(Miss.MODEL_C300, "hd", true))
        assertEquals("""{"videoquality":1,"enableaudio":0}""", Miss.startMediaBody("chuangmi.camera.077ac1", "sd", false))
        assertEquals("""{"videoquality":0,"enableaudio":1}""", Miss.startMediaBody("chuangmi.camera.077ac1", "auto", true))
    }

    @Test
    fun `PROTO-21 inner commands are BE u32 command plus body`() {
        val payload = Miss.innerCommand(Miss.CMD_VIDEO_START, "{}")
        assertEquals(0x102L, payload.beU32(0))
        assertEquals("{}", payload.copyOfRange(4, payload.size).decodeToString())

        val stop = Miss.innerCommand(Miss.CMD_VIDEO_STOP)
        assertEquals(4, stop.size)
        assertEquals(0x103L, stop.beU32(0))
    }

    @Test
    fun `PROTO-22 media headers parse little-endian size codec seq flags and u64 pts`() {
        val hdr = ByteArray(32)
        hdr.putLeU32(0, 512)
        hdr.putLeU32(4, Miss.CODEC_H265)
        hdr.putLeU32(8, 42)
        hdr.putLeU32(12, 0b1000)
        hdr.putLeU64(16, 5_000_000_000L) // > u32 to prove 64-bit pts
        val parsed = Miss.parseMediaHeader(hdr)
        assertEquals(512L, parsed.size)
        assertEquals(Miss.CODEC_H265, parsed.codecId)
        assertEquals(42L, parsed.sequence)
        assertEquals(0b1000L, parsed.flags)
        assertEquals(5_000_000_000L, parsed.ptsMs)
    }

    @Test
    fun `PROTO-23 codec ids map to frames and unknown ids are skipped`() {
        val data = byteArrayOf(1, 2, 3)
        fun header(codec: Long, flags: Long = 0) =
            Miss.MediaHeader(size = 3, codecId = codec, sequence = 1, flags = flags, ptsMs = 10)

        val h265 = Miss.frameFromPacket(header(Miss.CODEC_H265), data) as Frame.Video
        assertEquals("h265", h265.codec)
        assertContentEquals(data, h265.data)

        val h264 = Miss.frameFromPacket(header(Miss.CODEC_H264), data) as Frame.Video
        assertEquals("h264", h264.codec)

        val opus = Miss.frameFromPacket(header(Miss.CODEC_OPUS), data) as Frame.Audio
        assertEquals("opus", opus.codec)
        assertEquals(48000, opus.sampleRate)

        val pcma = Miss.frameFromPacket(header(Miss.CODEC_PCMA), data) as Frame.Audio
        assertEquals("pcma", pcma.codec)

        assertNull(Miss.frameFromPacket(header(9999), data))
    }

    @Test
    fun `PROTO-23 pcm sample rate derives from flags bits 3-6`() {
        assertEquals(8000, Miss.pcmSampleRate(0))
        assertEquals(16000, Miss.pcmSampleRate(0b1000))
        assertEquals(16000, Miss.pcmSampleRate(0b1110000))
        assertEquals(8000, Miss.pcmSampleRate(0b10000000)) // bit 7 is outside the field
    }

    @Test
    fun `PROTO-11 cameras are devices whose model contains camera`() {
        assertTrue(isCamera("chuangmi.camera.077ac1"))
        assertTrue(isCamera("isa.camera.hlc7"))
        assertEquals(false, isCamera("zhimi.airpurifier.ma2"))
        assertEquals(false, isCamera("yeelink.light.lamp4"))
    }
}
