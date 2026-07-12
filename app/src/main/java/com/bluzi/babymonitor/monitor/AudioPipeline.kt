package com.bluzi.babymonitor.monitor

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaFormat
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Opus → PCM → speaker, with an analysis tap. Mute only silences the speaker (LIVE-3):
 * decoding and the analysis callback keep running so the level meter and alarm stay live.
 */
class OpusAudioPlayer(
    private val sampleRate: Int = 48000,
    private val onPcmWindow: (pcm: ShortArray, sampleRate: Int) -> Unit,
) {
    private var codec: MediaCodec? = null
    private var track: AudioTrack? = null
    private val info = MediaCodec.BufferInfo()
    private val analysisBuf = ShortArray(2048)
    private var analysisLen = 0

    @Volatile
    var muted: Boolean = false
        set(value) {
            field = value
            track?.setVolume(if (value) 0f else 1f)
        }

    /** Never leaves a half-built codec or track behind: any failure releases what it created. */
    fun start() {
        try {
            open()
        } catch (e: Throwable) {
            release()
            throw e
        }
    }

    private fun open() {
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, sampleRate, 1)
        format.setByteBuffer("csd-0", ByteBuffer.wrap(opusHead(1, sampleRate)))
        // Pre-skip and seek-preroll (ns, LE u64). Zero: live stream, nothing to trim.
        format.setByteBuffer("csd-1", ByteBuffer.wrap(ByteArray(8)))
        format.setByteBuffer("csd-2", ByteBuffer.wrap(ByteArray(8)))
        val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS)
        codec = c // publish before configure/start: release() can only free what it can see
        c.configure(format, null, null, 0)
        c.start()

        val minBuf = AudioTrack.getMinBufferSize(
            sampleRate,
            AudioFormat.CHANNEL_OUT_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
        )
        val t = AudioTrack(
            AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                .build(),
            AudioFormat.Builder()
                .setSampleRate(sampleRate)
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                .build(),
            maxOf(minBuf * 4, 16 * 1024),
            AudioTrack.MODE_STREAM,
            AudioManager.AUDIO_SESSION_ID_GENERATE,
        )
        track = t // published before play(), which can still throw on a busy audio HAL
        t.setVolume(if (muted) 0f else 1f)
        t.play()
    }

    fun push(packet: ByteArray, ptsMs: Long) {
        val c = codec ?: return
        val inIdx = c.dequeueInputBuffer(20_000)
        if (inIdx >= 0) {
            val buf = c.getInputBuffer(inIdx)!!
            buf.clear()
            buf.put(packet)
            c.queueInputBuffer(inIdx, 0, packet.size, ptsMs * 1000, 0)
        }
        drain()
    }

    private fun drain() {
        val c = codec ?: return
        while (true) {
            val outIdx = c.dequeueOutputBuffer(info, 0)
            if (outIdx < 0) return
            val out = c.getOutputBuffer(outIdx)!!
            out.order(ByteOrder.LITTLE_ENDIAN)
            val samples = ShortArray(info.size / 2)
            out.position(info.offset)
            out.asShortBuffer().get(samples)
            c.releaseOutputBuffer(outIdx, false)

            // A dead AudioTrack (audioserver restart) returns an error forever and plays nothing:
            // the parent would hear silence and read it as a quiet room. Fail loudly instead —
            // the engine reconnects and rebuilds the pipeline (LIVE-5).
            val written = track?.write(samples, 0, samples.size) ?: 0
            if (written < 0) throw IllegalStateException("audio output failed (code $written)")
            analyze(samples)
        }
    }

    private fun analyze(samples: ShortArray) {
        var off = 0
        while (off < samples.size) {
            val n = minOf(analysisBuf.size - analysisLen, samples.size - off)
            samples.copyInto(analysisBuf, analysisLen, off, off + n)
            analysisLen += n
            off += n
            if (analysisLen == analysisBuf.size) {
                onPcmWindow(analysisBuf.copyOf(), sampleRate)
                analysisLen = 0
            }
        }
    }

    /**
     * Frees the codec and the track, whatever state they are in. Each step is guarded on its own:
     * a half-built codec throws on stop(), and if that skipped release() we would leak a decoder
     * on every reconnect until the phone had none left — silent, permanent deafness.
     */
    fun release() {
        val c = codec
        codec = null
        if (c != null) {
            try { c.stop() } catch (_: Exception) {}
            try { c.release() } catch (_: Exception) {}
        }
        val t = track
        track = null
        if (t != null) {
            try { t.stop() } catch (_: Exception) {}
            try { t.release() } catch (_: Exception) {}
        }
    }

    private companion object {
        /** Minimal OpusHead (RFC 7845 §5.1) for MediaCodec's csd-0. */
        fun opusHead(channels: Int, sampleRate: Int): ByteArray {
            val head = ByteBuffer.allocate(19).order(ByteOrder.LITTLE_ENDIAN)
            head.put("OpusHead".toByteArray())
            head.put(1) // version
            head.put(channels.toByte())
            head.putShort(0) // pre-skip
            head.putInt(sampleRate)
            head.putShort(0) // output gain
            head.put(0) // mapping family
            return head.array()
        }
    }
}
