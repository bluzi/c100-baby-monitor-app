package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.AppStore
import com.bluzi.babymonitor.data.KeyValueStore
import com.bluzi.babymonitor.data.SecretBox
import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.json.JSONObject
import com.bluzi.babymonitor.net.Datagram
import com.bluzi.babymonitor.net.MiHttp
import com.bluzi.babymonitor.net.RawResponse
import com.bluzi.babymonitor.net.SocketFactory
import com.bluzi.babymonitor.net.TcpSocket
import com.bluzi.babymonitor.net.UdpSocket
import com.bluzi.babymonitor.runRealTimeTest
import com.bluzi.babymonitor.xiaomi.Crypto
import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.Session
import com.bluzi.babymonitor.xiaomi.base64ToBytes
import com.bluzi.babymonitor.xiaomi.toBase64
import com.bluzi.babymonitor.xiaomi.toHex
import com.bluzi.babymonitor.xiaomi.urlDecode
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeout
import kotlin.concurrent.Volatile
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * The engine's lifecycle, against a scripted camera.
 *
 * This exists because LIVE-18 is not a Windows criterion any more: the picture quality is chosen when
 * the stream is asked for, so changing it has to end the session and ask again — on every platform.
 * The C# port's suite has pinned that behaviour; this is the same criterion, on the core the phone,
 * the Mac and the iPhone actually run.
 */
class MonitorEngineTest {
    private val kv = MemoryKv()
    private val store = AppStore(kv, PassthroughBox())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var engine: MonitorEngine? = null

    init {
        store.saveSession(Session("U1", "CU1", "PT1", "ST1", SSECURITY.base64ToBytes(), "de"))
        store.saveDevice(Device("did-1", "Nursery", "chuangmi.camera.077ac1", "AA:BB", "192.0.2.1"))
    }

    @AfterTest
    fun tearDown() {
        engine?.stop()
        scope.cancel()
        MonitorHub.running.value = false
        MonitorHub.status.value = STATUS_STOPPED
        MonitorHub.activeAlarm.value = null
        MonitorHub.applySettings(Settings())
    }

    @Test
    fun `LIVE-18 changing the picture quality re-asks the camera — the feed reconnects`() = runRealTimeTest {
        // A parent who picks SD under a running stream must get a NEW session. The camera cannot be
        // asked to change its mind mid-stream — it was told which picture to send when the stream was
        // asked for — so a control that only wrote the setting would do nothing at all until the next
        // reconnect, which may be hours away or never.
        //
        // The ask itself is encrypted on the wire (MISS/ChaCha20) so what is counted here is sessions;
        // that "sd" maps to the right videoquality on the wire is PROTO-21's job.
        val camera = ScriptedCamera()
        val e = MonitorEngine(store, scope, RecordingRinger(), NullMedia(), camera, cameraCloud())
        engine = e

        e.start()
        camera.awaitSessions(1)

        store.saveSettings(store.loadSettings().copy(videoQuality = Settings.QUALITY_SD))
        MonitorHub.applySettings(store.loadSettings())

        // A second session, with nothing having failed — the reconnect the control promises.
        camera.awaitSessions(2)
    }

    @Test
    fun `LIVE-18 choosing the quality already in use disturbs nothing`() = runRealTimeTest {
        // Re-picking HD while HD is playing must not drop the sound. A monitor that goes quiet for no
        // reason is worse than one that never offered the control at all.
        val camera = ScriptedCamera()
        val e = MonitorEngine(store, scope, RecordingRinger(), NullMedia(), camera, cameraCloud())
        engine = e

        e.start()
        camera.awaitSessions(1)

        // The settings do change — mute moves — but the picture's size does not.
        store.saveSettings(store.loadSettings().copy(videoQuality = Settings.QUALITY_HD, muted = true))
        MonitorHub.applySettings(store.loadSettings())
        delay(400)

        assertEquals(1, camera.sessions)
    }

    // --- the scripted camera -------------------------------------------------

    /**
     * A camera that completes the CS2 handshake and the MISS session **as often as it is asked to** —
     * a fresh socket per session. One session is all a handshake test needs; a reconnect needs the
     * camera to still be there the second time, which is what LIVE-18 turns on.
     */
    private class ScriptedCamera : SocketFactory {
        private val counted = Channel<Int>(Channel.UNLIMITED)

        // The engine runs one session at a time, so this is only ever written by whichever session's
        // socket is currently alive — never by two at once.
        @Volatile
        private var count = 0

        val sessions: Int get() = count

        override fun udp(): UdpSocket = PunchingUdp()

        override fun tcp(): TcpSocket = SessionTcp(::onMediaAsked)

        private fun onMediaAsked() {
            counted.trySend(++count)
        }

        /** Suspends until the camera has been asked for media [n] times. */
        suspend fun awaitSessions(n: Int) {
            withTimeout(20_000) {
                while (count < n) {
                    counted.receive()
                }
            }
        }
    }

    /** PROTO-15: the punch packet, then "use TCP". */
    private class PunchingUdp : UdpSocket {
        private val incoming = Channel<Datagram>(Channel.UNLIMITED)

        init {
            incoming.trySend(Datagram(byteArrayOf(0xf1.toByte(), 0x41, 0, 4, 9, 9, 9, 9), "192.0.2.1", 32108))
            incoming.trySend(Datagram(byteArrayOf(0xf1.toByte(), 0x43, 0, 0), "192.0.2.1", 32108))
        }

        override suspend fun bind() {}
        override suspend fun send(data: ByteArray, host: String, port: Int) {}
        override suspend fun receive(): Datagram = incoming.receive()
        override fun close() {
            incoming.close()
        }
    }

    /** One session's socket: answers the auth, then counts the ask for media. */
    private class SessionTcp(private val onMediaAsked: () -> Unit) : TcpSocket {
        private val incoming = Channel<Byte>(Channel.UNLIMITED)
        private var writes = 0

        init {
            // PROTO-20: the MISS session says yes.
            for (b in tcpFrame(drw(authOk()))) incoming.trySend(b)
        }

        override suspend fun connect(host: String, port: Int) {}

        override suspend fun write(data: ByteArray) {
            // The first write is the auth request; the second is startMedia — by then the monitor is on
            // the camera and this session is real. (The rest are keepalives and the goodbye.)
            if (++writes == 2) onMediaAsked()
        }

        override suspend fun readExact(n: Int): ByteArray = ByteArray(n) { incoming.receive() }

        override fun close() {
            // Closing is how the engine ends a session to re-ask at another quality: the reader has to
            // come undone rather than block for ever, or the reconnect never happens.
            incoming.close()
        }
    }

    private companion object {
        const val SSECURITY = "TGhlIHNlY3JldCBrZXkhIQ=="

        /** A cloud that hands out a real camera: vendor cs2, a device key, a sign token. */
        fun cameraCloud(): MiHttp = object : MiHttp {
            override suspend fun request(
                url: String,
                method: String,
                headers: Map<String, String>,
                body: String?,
            ): RawResponse {
                if (!url.endsWith("/v2/device/miss_get_vendor")) {
                    // The device-list refresh may fail: the engine falls back to the stored address.
                    throw XiaomiTestException("the device list is not part of this test")
                }
                val ssecurity = SSECURITY.base64ToBytes()
                val form = parseForm(body!!)
                val nonce = form["_nonce"]!!.base64ToBytes()
                val signedNonce = Crypto.genSignedNonce(ssecurity, nonce)
                // Any 32-byte curve point will do — the handshake never leaves this process.
                val devicePublic = ByteArray(32) { 7 }.toHex()
                val result = """{"code":0,"result":{
                    "vendor":{"vendor":4,"vendor_params":{"p2p_id":"ABC-123"}},
                    "public_key":"$devicePublic","sign":"SIGN-TOKEN"
                }}"""
                val enc = Crypto.rc4(signedNonce, result.encodeToByteArray()).toBase64().encodeToByteArray()
                return RawResponse(200, "", emptyList(), enc)
            }
        }

        fun parseForm(body: String): Map<String, String> =
            body.split("&").associate {
                val (k, v) = it.split("=", limit = 2)
                urlDecode(k) to urlDecode(v)
            }

        /** One command record: [BE u32 len][LE u32 cmd][payload]. */
        fun authOk(): ByteArray {
            val payload = """{"result":"success"}""".encodeToByteArray()
            val record = ByteArray(8 + payload.size)
            putBeU32(record, 0, 4 + payload.size)
            putLeU32(record, 4, 0x100)
            payload.copyInto(record, 8)
            return record
        }

        fun drw(chunk: ByteArray, channel: Int = 0, seq: Int = 0): ByteArray {
            val b = ByteArray(8 + chunk.size)
            b[0] = 0xf1.toByte()
            b[1] = 0xd0.toByte()
            putBeU16(b, 2, 4 + 4 + chunk.size)
            b[4] = 0xd1.toByte()
            b[5] = channel.toByte()
            putBeU16(b, 6, seq)
            chunk.copyInto(b, 8)
            return b
        }

        fun tcpFrame(payload: ByteArray): ByteArray {
            val b = ByteArray(4 + payload.size)
            putBeU32(b, 0, payload.size)
            payload.copyInto(b, 4)
            return b
        }

        fun putBeU32(b: ByteArray, off: Int, v: Int) {
            b[off] = (v ushr 24).toByte()
            b[off + 1] = (v ushr 16).toByte()
            b[off + 2] = (v ushr 8).toByte()
            b[off + 3] = v.toByte()
        }

        fun putLeU32(b: ByteArray, off: Int, v: Int) {
            b[off] = v.toByte()
            b[off + 1] = (v ushr 8).toByte()
            b[off + 2] = (v ushr 16).toByte()
            b[off + 3] = (v ushr 24).toByte()
        }

        fun putBeU16(b: ByteArray, off: Int, v: Int) {
            b[off] = (v ushr 8).toByte()
            b[off + 1] = v.toByte()
        }
    }
}

private class XiaomiTestException(message: String) : Exception(message)

private class MemoryKv : KeyValueStore {
    private val map = mutableMapOf<String, String>()
    override fun get(key: String): String? = map[key]
    override fun put(key: String, value: String) {
        map[key] = value
    }

    override fun remove(key: String) {
        map.remove(key)
    }
}

private class PassthroughBox : SecretBox {
    override fun seal(plain: String): String = plain
    override fun open(sealed: String): String? = sealed
}

private class RecordingRinger : Ringer {
    override fun ring(kind: AlarmKind, cameraName: String): Boolean = true
    override fun acknowledge() {}
}

private class NullMedia : MediaFactory {
    override fun audio(onPcmWindow: (pcm: ShortArray, sampleRate: Int) -> Unit): AudioOutput =
        object : AudioOutput {
            override var muted: Boolean = false
            override fun start() {}
            override fun push(packet: ByteArray, ptsMs: Long) {}
            override fun release() {}
        }

    override fun video(): VideoOutput = object : VideoOutput {
        override fun push(annexB: ByteArray, ptsMs: Long) {}
        override fun release() {}
    }
}
