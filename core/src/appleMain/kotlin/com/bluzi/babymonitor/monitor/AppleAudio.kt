package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.log.Log
import copus.OPUS_OK
import copus.opus_decode
import copus.opus_decoder_create
import copus.opus_decoder_destroy
import copus.opus_strerror
import kotlin.concurrent.Volatile
import kotlinx.cinterop.CPointer
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.IntVar
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.alloc
import kotlinx.cinterop.convert
import kotlinx.cinterop.get
import kotlinx.cinterop.memScoped
import kotlinx.cinterop.ptr
import kotlinx.cinterop.reinterpret
import kotlinx.cinterop.toKString
import kotlinx.cinterop.usePinned
import kotlinx.cinterop.value
import platform.AVFAudio.AVAudioEngine
import platform.AVFAudio.AVAudioFormat
import platform.AVFAudio.AVAudioPCMBuffer
import platform.AVFAudio.AVAudioPCMFormatInt16
import platform.AVFAudio.AVAudioPlayerNode
import platform.posix.memcpy

private const val TAG = "audio"

/**
 * The Mac's [AudioOutput]: Opus → libopus → AVAudioEngine, with the analysis tap.
 *
 * This whole path is Kotlin on purpose. It is the crying alarm's path — every decoded window feeds
 * the level meter and the detector — and putting a language bridge in the middle of it would put a
 * bridge between a crying baby and a sleeping parent. Swift renders the picture; it does not touch
 * the sound.
 *
 * No Apple framework decodes Opus, so libopus is linked in statically (see core/build.gradle.kts).
 *
 * LIVE-3: muting sets the player's volume to zero. Decoding and [onPcmWindow] carry on regardless —
 * that is what keeps the level meter and the alarm alive behind a muted feed.
 */
@OptIn(ExperimentalForeignApi::class)
class AppleAudioOutput(
    private val sampleRate: Int = 48000,
    private val onPcmWindow: (pcm: ShortArray, sampleRate: Int) -> Unit,
) : AudioOutput {
    private companion object {
        /** 120 ms at 48 kHz — the largest frame Opus can carry, so a packet never overruns. */
        const val MAX_FRAME_SAMPLES = 5760
    }

    private var decoder: CPointer<cnames.structs.OpusDecoder>? = null
    private var engine: AVAudioEngine? = null
    private var player: AVAudioPlayerNode? = null
    private var format: AVAudioFormat? = null

    private val analysisBuf = ShortArray(2048)
    private var analysisLen = 0
    private val decoded = ShortArray(MAX_FRAME_SAMPLES)

    @Volatile
    override var muted: Boolean = false
        set(value) {
            field = value
            player?.volume = if (value) 0f else 1f
        }

    /** Never leaves a half-built decoder or engine behind: any failure releases what it created. */
    override fun start() {
        try {
            open()
        } catch (e: Throwable) {
            release()
            throw e
        }
    }

    private fun open() = memScoped {
        val error = alloc<IntVar>()
        val dec = opus_decoder_create(sampleRate, 1, error.ptr)
        if (dec == null || error.value != OPUS_OK) {
            throw IllegalStateException("opus: ${opus_strerror(error.value)?.toKString() ?: "decoder failed"}")
        }
        decoder = dec

        val fmt = AVAudioFormat(
            commonFormat = AVAudioPCMFormatInt16,
            sampleRate = sampleRate.toDouble(),
            channels = 1u,
            interleaved = false,
        ) ?: throw IllegalStateException("audio: could not build the output format")
        format = fmt

        val e = AVAudioEngine()
        val p = AVAudioPlayerNode()
        engine = e // published before start(): release() can only free what it can see
        player = p
        e.attachNode(p)
        e.connect(p, e.mainMixerNode, fmt)

        memScoped {
            val err = alloc<kotlinx.cinterop.ObjCObjectVar<platform.Foundation.NSError?>>()
            if (!e.startAndReturnError(err.ptr)) {
                throw IllegalStateException("audio: engine would not start: ${err.value?.localizedDescription}")
            }
        }
        p.volume = if (muted) 0f else 1f
        p.play()
        Log.i(TAG, "opus decoder + audio engine up (${sampleRate}Hz mono)")
    }

    override fun push(packet: ByteArray, ptsMs: Long) {
        val dec = decoder ?: return
        val samples = packet.usePinned { inPin ->
            decoded.usePinned { outPin ->
                opus_decode(
                    dec,
                    inPin.addressOf(0).reinterpret(),
                    packet.size.convert(),
                    outPin.addressOf(0),
                    MAX_FRAME_SAMPLES,
                    0,
                )
            }
        }
        if (samples <= 0) {
            // A corrupt packet is a blip, not a failure — the next one usually decodes. Losing the
            // whole connection over one bad packet would be worse than a click in the audio.
            Log.w(TAG, "opus: dropped a packet (${opus_strerror(samples)?.toKString()})")
            return
        }
        val pcm = decoded.copyOf(samples)
        playback(pcm)
        analyze(pcm)
    }

    private fun playback(pcm: ShortArray) {
        val p = player ?: return
        val fmt = format ?: return
        val buffer = AVAudioPCMBuffer(fmt, pcm.size.convert())
            ?: throw IllegalStateException("audio: could not allocate an output buffer")
        buffer.frameLength = pcm.size.convert()
        val channel = buffer.int16ChannelData?.get(0)
        // A dead output path must fail loudly: the parent would hear silence and read it as a
        // quiet room. The engine reconnects and rebuilds (LIVE-5).
            ?: throw IllegalStateException("audio: output buffer has no channel data")
        pcm.usePinned { memcpy(channel, it.addressOf(0), (pcm.size * 2).convert()) }
        p.scheduleBuffer(buffer, null)
    }

    /** LIVE-3: runs muted or not — the level meter and the alarm feed off this. */
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
     * Frees the decoder and the engine, whatever state they are in. Each step is guarded on its
     * own: leaking a decoder on every reconnect would, over a night of retries, end in a monitor
     * with no audio at all — silent, permanent deafness.
     */
    override fun release() {
        val p = player
        player = null
        if (p != null) {
            runCatching { p.stop() }
        }
        val e = engine
        engine = null
        if (e != null) {
            runCatching { e.stop() }
        }
        val d = decoder
        decoder = null
        if (d != null) {
            runCatching { opus_decoder_destroy(d) }
        }
        format = null
    }
}
