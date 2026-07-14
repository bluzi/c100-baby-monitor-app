package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.json.JSONObject
import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.net.SocketFactory

// MISS (Mi Secure Streaming) — port of c100/src/xiaomi/miss.ts.
// Rides on CS2: command channel 0 for control, media channel 2 for A/V.

object Miss {
    const val CMD_AUTH_REQ = 0x100L
    const val CMD_VIDEO_START = 0x102L
    const val CMD_VIDEO_STOP = 0x103L
    const val CMD_ENCODED = 0x1001L

    const val CODEC_H264 = 4L
    const val CODEC_H265 = 5L
    const val CODEC_PCM = 1024L
    const val CODEC_PCMU = 1026L
    const val CODEC_PCMA = 1027L
    const val CODEC_OPUS = 1032L

    const val MODEL_C200 = "chuangmi.camera.046c04"
    const val MODEL_C300 = "chuangmi.camera.72ac1"

    /** PROTO-20: the (unencrypted) MISS auth payload. */
    fun authPayload(clientPublicHex: String, sign: String): String =
        JSONObject()
            .put("public_key", clientPublicHex)
            .put("sign", sign)
            .put("uuid", "")
            .put("support_encrypt", 0)
            .toString()

    /** PROTO-21: quality mapping + start body. */
    fun startMediaBody(model: String, quality: String, audio: Boolean): String {
        val q = when (quality) {
            "sd" -> "1"
            "auto" -> "0"
            else -> if (model == MODEL_C200 || model == MODEL_C300) "3" else "2"
        }
        val a = if (audio) "1" else "0"
        return """{"videoquality":$q,"enableaudio":$a}"""
    }

    /** PROTO-21: [BE u32 inner command][body] — the plaintext of an encoded control command. */
    fun innerCommand(cmd: Long, body: String = ""): ByteArray {
        val bodyBytes = body.encodeToByteArray()
        val payload = ByteArray(4 + bodyBytes.size)
        payload.putBeU32(0, cmd)
        bodyBytes.copyInto(payload, 4)
        return payload
    }

    /** PROTO-22: parse the 32-byte little-endian media header. */
    data class MediaHeader(val size: Long, val codecId: Long, val sequence: Long, val flags: Long, val ptsMs: Long)

    fun parseMediaHeader(hdr: ByteArray): MediaHeader {
        require(hdr.size >= 32) { "miss: header too small" }
        return MediaHeader(
            size = hdr.leU32(0),
            codecId = hdr.leU32(4),
            sequence = hdr.leU32(8),
            flags = hdr.leU32(12),
            ptsMs = hdr.leU64(16),
        )
    }

    /** PROTO-23: PCM-family sample rate is derived from flags bits 3–6. */
    fun pcmSampleRate(flags: Long): Int = if ((flags shr 3) and 0b1111L != 0L) 16000 else 8000

    /** PROTO-23: decode one decrypted media packet into a Frame (null = skip). */
    fun frameFromPacket(header: MediaHeader, payload: ByteArray): Frame? = when (header.codecId) {
        CODEC_H264 -> Frame.Video("h264", header.ptsMs, header.sequence, header.flags, payload)
        CODEC_H265 -> Frame.Video("h265", header.ptsMs, header.sequence, header.flags, payload)
        CODEC_OPUS -> Frame.Audio("opus", 48000, header.ptsMs, header.sequence, header.flags, payload)
        CODEC_PCMA, CODEC_PCMU, CODEC_PCM -> Frame.Audio(
            when (header.codecId) {
                CODEC_PCMA -> "pcma"
                CODEC_PCMU -> "pcmu"
                else -> "pcm"
            },
            pcmSampleRate(header.flags),
            header.ptsMs,
            header.sequence,
            header.flags,
            payload,
        )
        else -> null
    }
}

class MissClient(private val model: String, sockets: SocketFactory) {
    lateinit var key: ByteArray // 32-byte NaCl shared key (PROTO-13)
    val conn = Cs2Conn(sockets)

    data class ConnectParams(
        val ip: String,
        val vendor: String,
        val transport: String? = "tcp",
        val clientPublic: ByteArray,
        val clientPrivate: ByteArray,
        val devicePublicHex: String,
        val sign: String,
    )

    suspend fun connect(params: ConnectParams) {
        if (params.vendor != "cs2") {
            throw XiaomiException("miss: unsupported vendor ${params.vendor} (only cs2 implemented)")
        }
        Log.i("miss", "connect ip=${params.ip} model=$model transport=${params.transport}")
        key = Crypto.calcSharedKey(params.devicePublicHex, params.clientPrivate.toHex())
        conn.dial(params.ip, params.transport)
        login(params.clientPublic.toHex(), params.sign)
    }

    private suspend fun login(clientPublicHex: String, sign: String) {
        Log.i("miss", "authenticating…")
        conn.writeCommand(Miss.CMD_AUTH_REQ, Miss.authPayload(clientPublicHex, sign).encodeToByteArray())
        val (_, data) = conn.readCommand()
        val text = data.decodeToString()
        if (!text.contains("\"result\":\"success\"")) {
            Log.w("miss", "auth failed: ${text.take(200)}")
            throw XiaomiException("miss: auth failed: $text")
        }
        Log.i("miss", "auth ok")
    }

    suspend fun startMedia(quality: String = "hd", audio: Boolean = true) {
        Log.i("miss", "startMedia quality=$quality audio=$audio")
        writeEncoded(Miss.innerCommand(Miss.CMD_VIDEO_START, Miss.startMediaBody(model, quality, audio)))
    }

    suspend fun stopMedia() {
        writeEncoded(Miss.innerCommand(Miss.CMD_VIDEO_STOP))
    }

    private suspend fun writeEncoded(data: ByteArray) {
        conn.writeCommand(Miss.CMD_ENCODED, Crypto.chachaEncode(data, key))
    }

    /**
     * Read the next decrypted media frame. Skips unknown codecs and packets that fail to
     * decrypt (PROTO-23); throws only when the connection is gone.
     */
    suspend fun readFrame(): Frame {
        while (true) {
            val (hdr, encPayload) = conn.readPacket()
            val header = Miss.parseMediaHeader(hdr)
            val payload = try {
                Crypto.chachaDecode(encPayload, key)
            } catch (_: Exception) {
                continue
            }
            return Miss.frameFromPacket(header, payload) ?: continue
        }
    }

    fun close() {
        conn.close()
    }
}
