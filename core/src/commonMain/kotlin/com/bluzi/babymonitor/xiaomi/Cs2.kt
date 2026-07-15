package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.net.SocketFactory
import com.bluzi.babymonitor.net.TcpSocket
import com.bluzi.babymonitor.net.UdpSocket
import com.bluzi.babymonitor.platform.ioDispatcher
import kotlin.concurrent.Volatile
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout

// CS2 P2P transport — port of c100/src/xiaomi/cs2.ts (itself pkg/xiaomi/miss/cs2/conn.go).
// UDP handshake on :32108, then TCP (the C100 default) or UDP for the data phase.

object Cs2 {
    const val MAGIC = 0xf1
    const val MAGIC_DRW = 0xd1
    const val MAGIC_TCP = 0x68
    const val MSG_LAN_SEARCH = 0x30
    const val MSG_PUNCH_PKT = 0x41
    const val MSG_P2P_RDY_UDP = 0x42
    const val MSG_P2P_RDY_TCP = 0x43
    const val MSG_DRW = 0xd0
    const val MSG_DRW_ACK = 0xd1
    const val MSG_PING = 0xe0
    const val MSG_PONG = 0xe1
    const val HANDSHAKE_PORT = 32108

    val LAN_SEARCH = byteArrayOf(MAGIC.toByte(), MSG_LAN_SEARCH.toByte(), 0, 0)
    val PING = byteArrayOf(MAGIC.toByte(), MSG_PING.toByte(), 0, 0)

    /** PROTO-17: DRW frame carrying a command record (BE u32 size + LE u32 cmd + payload). */
    fun marshalCmd(channel: Int, seq: Int, cmd: Long, payload: ByteArray): ByteArray {
        val req = ByteArray(16 + payload.size)
        req[0] = MAGIC.toByte()
        req[1] = MSG_DRW.toByte()
        req.putBeU16(2, 12 + payload.size)
        req[4] = MAGIC_DRW.toByte()
        req[5] = channel.toByte()
        req.putBeU16(6, seq)
        req.putBeU32(8, (4 + payload.size).toLong())
        req.putBeU32(12, cmd)
        payload.copyInto(req, 16)
        return req
    }

    /** PROTO-16: TCP framing — 8-byte header, BE u16 size at 0, magic 0x68 at 2. */
    fun tcpFrame(body: ByteArray): ByteArray {
        val buf = ByteArray(8 + body.size)
        buf.putBeU16(0, body.size)
        buf[2] = MAGIC_TCP.toByte()
        body.copyInto(buf, 8)
        return buf
    }

    fun udpAck(channel: Int, seqHi: Byte, seqLo: Byte): ByteArray = byteArrayOf(
        MAGIC.toByte(), MSG_DRW_ACK.toByte(), 0, 6,
        MAGIC_DRW.toByte(), channel.toByte(), 0, 1, seqHi, seqLo,
    )
}

/**
 * PROTO-18: reassembles 4-byte BE length-prefixed records out of the DRW payload byte stream,
 * regardless of how the bytes were split across DRW packets.
 */
class RecordAssembler {
    companion object {
        /**
         * PROTO-18: no legitimate record comes close (commands are small, a keyframe is tens of
         * KB). A prefix past this is corrupt or hostile input — the connection is dead. Without
         * the check, a length ≥ 2^31 becomes a negative Int and crashes the process instead.
         */
        const val MAX_RECORD_BYTES = 8 * 1024 * 1024
    }

    private var pending = ByteArray(0)
    private var waitSize = 0

    fun push(chunk: ByteArray): List<ByteArray> {
        pending = concatBytes(pending, chunk)
        val out = mutableListOf<ByteArray>()
        while (true) {
            if (waitSize == 0) {
                if (pending.size < 4) break
                val size = pending.beU32(0)
                if (size > MAX_RECORD_BYTES) {
                    throw XiaomiException("cs2: corrupt record length $size — dropping the connection")
                }
                waitSize = size.toInt()
                pending = pending.copyOfRange(4, pending.size)
                continue // a zero-length record is consumed by its prefix alone
            }
            // A record completed by even a 1-byte chunk must come out now — a small command
            // record must never sit here waiting for unrelated bytes to flush it.
            if (pending.size < waitSize) break
            out.add(pending.copyOfRange(0, waitSize))
            pending = pending.copyOfRange(waitSize, pending.size)
            waitSize = 0
        }
        return out
    }
}

private interface RawConn {
    val isTcp: Boolean
    suspend fun write(data: ByteArray)
    suspend fun read(): ByteArray
    fun close()
}

class Cs2Conn(private val sockets: SocketFactory) {
    private var raw: RawConn? = null
    private var seqCh0 = 0
    private val writeMutex = Mutex()
    private val scope = CoroutineScope(SupervisorJob() + ioDispatcher)
    private var workerJob: Job? = null

    // PROTO-18: BOTH channels carry length-prefixed records that span DRW frames.
    private val ch0Assembler = RecordAssembler()
    private val ch2Assembler = RecordAssembler()
    val channel0 = Channel<ByteArray>(Channel.UNLIMITED) // command records
    val channel2 = Channel<ByteArray>(Channel.UNLIMITED) // media records

    @Volatile
    private var closed = false

    val isTcp: Boolean get() = raw?.isTcp == true

    /** PROTO-15: LAN search → punch echo → P2P ready, then TCP or UDP data phase. */
    suspend fun dial(host: String, transport: String? = "tcp") {
        Log.i("cs2", "dial $host:${Cs2.HANDSHAKE_PORT} transport=$transport")
        val udp = sockets.udp()
        udp.bind()
        var remotePort = Cs2.HANDSHAKE_PORT

        suspend fun writeUntil(req: ByteArray, accept: (ByteArray) -> Boolean): ByteArray =
            withTimeout(5000) {
                // First send is synchronous so it always targets the current port;
                // the retransmitter only handles lost packets after that.
                try {
                    udp.send(req, host, remotePort)
                } catch (_: Exception) {
                }
                val sender = scope.launch {
                    while (true) {
                        delay(1000)
                        try {
                            udp.send(req, host, remotePort)
                        } catch (_: Exception) {
                        }
                    }
                }
                try {
                    while (true) {
                        val dg = try {
                            udp.receive()
                        } catch (e: CancellationException) {
                            throw e // the handshake timeout, or a real stop — never swallowed
                        } catch (e: Exception) {
                            // PROTO-25: a transient read failure mid-handshake is not a dead
                            // connection. On Windows the camera's ICMP port-unreachable surfaces as a
                            // connection-reset on the very next receive; abort on it and the monitor
                            // never leaves the reconnect loop. Keep waiting within the timeout — the
                            // retransmitter is still firing — with a short pause so a genuinely dead
                            // socket cannot spin.
                            Log.d("cs2", "ignoring a transient udp read failure during handshake: ${e.message}")
                            delay(50)
                            continue
                        }
                        if (dg.host != host || dg.data.size < 4) continue
                        if (accept(dg.data)) {
                            remotePort = dg.port
                            return@withTimeout dg.data
                        }
                    }
                    @Suppress("UNREACHABLE_CODE")
                    throw XiaomiException("unreachable")
                } finally {
                    sender.cancel()
                }
            }

        val punch = try {
            writeUntil(Cs2.LAN_SEARCH) { it[1].toInt() and 0xff == Cs2.MSG_PUNCH_PKT }
        } catch (e: Exception) {
            udp.close()
            Log.w("cs2", "handshake timed out at LAN search — camera $host unreachable on :${Cs2.HANDSHAKE_PORT}? (same Wi-Fi?)")
            throw XiaomiException("cs2: handshake timeout (LAN search)", e)
        }
        Log.i("cs2", "got punch packet from $host:$remotePort")

        val wantUdp = transport == null || transport == "udp"
        val wantTcp = transport == null || transport == "tcp"
        val ready = try {
            writeUntil(punch) {
                val msg = it[1].toInt() and 0xff
                (wantUdp && msg == Cs2.MSG_P2P_RDY_UDP) || (wantTcp && msg == Cs2.MSG_P2P_RDY_TCP)
            }
        } catch (e: Exception) {
            udp.close()
            Log.w("cs2", "handshake timed out waiting for P2P-ready from $host")
            throw XiaomiException("cs2: handshake timeout (P2P ready)", e)
        }

        raw = if (ready[1].toInt() and 0xff == Cs2.MSG_P2P_RDY_TCP) {
            Log.i("cs2", "P2P ready (TCP) — connecting tcp $host:$remotePort")
            udp.close()
            openTcp(host, remotePort)
        } else {
            Log.i("cs2", "P2P ready (UDP) on $host:$remotePort")
            wrapUdp(udp, host, remotePort)
        }
        Log.i("cs2", "transport up: ${if (raw!!.isTcp) "cs2+tcp" else "cs2+udp"}")

        workerJob = scope.launch { workerLoop() }

        // PROTO-19: keepalive on an independent timer; never answer PING with PONG.
        if (raw!!.isTcp) {
            scope.launch {
                while (!closed) {
                    delay(1000)
                    if (closed) break
                    try {
                        writeRaw(Cs2.PING)
                    } catch (_: Exception) {
                    }
                }
            }
        }
    }

    private fun wrapUdp(udp: UdpSocket, host: String, port: Int): RawConn = object : RawConn {
        override val isTcp = false
        override suspend fun write(data: ByteArray) = udp.send(data, host, port)
        override suspend fun read(): ByteArray {
            while (true) {
                val dg = udp.receive()
                if (dg.host == host) return dg.data
            }
        }

        override fun close() = udp.close()
    }

    private suspend fun openTcp(host: String, port: Int): RawConn {
        val tcp: TcpSocket = sockets.tcp()
        tcp.connect(host, port)
        return object : RawConn {
            override val isTcp = true
            override suspend fun write(data: ByteArray) = tcp.write(Cs2.tcpFrame(data))
            override suspend fun read(): ByteArray {
                val hdr = tcp.readExact(8)
                return tcp.readExact(hdr.beU16(0))
            }

            override fun close() = tcp.close()
        }
    }

    private suspend fun workerLoop() {
        val conn = raw ?: return
        while (!closed) {
            // Anything thrown here — a failed read, or a corrupt record from the assembler
            // (PROTO-18) — is connection-fatal, never process-fatal: this scope has no exception
            // handler, so an escaped throw would kill the whole app.
            try {
                val buf = conn.read()
                if (buf.size < 4) continue
                when (buf[1].toInt() and 0xff) {
                    Cs2.MSG_DRW -> {
                        if (buf.size < 8) continue
                        val ch = buf[5].toInt() and 0xff
                        val payload = buf.copyOfRange(8, buf.size)
                        if (!conn.isTcp) {
                            try {
                                conn.write(Cs2.udpAck(ch, buf[6], buf[7]))
                            } catch (_: Exception) {
                            }
                        }
                        when (ch) {
                            0 -> for (rec in ch0Assembler.push(payload)) channel0.trySend(rec)
                            2 -> for (rec in ch2Assembler.push(payload)) channel2.trySend(rec)
                        }
                    }
                    // PROTO-19: PING is deliberately not answered.
                    else -> {}
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                if (!closed) {
                    Log.w("cs2", "connection lost: ${e.message}")
                    channel0.close(XiaomiException("cs2: connection lost", e))
                    channel2.close(XiaomiException("cs2: connection lost", e))
                }
                return
            }
        }
    }

    suspend fun writeCommand(cmd: Long, data: ByteArray) {
        writeMutex.withLock {
            val req = Cs2.marshalCmd(0, seqCh0, cmd, data)
            seqCh0 = (seqCh0 + 1) and 0xffff
            raw?.write(req) ?: throw XiaomiException("cs2: not connected")
        }
    }

    private suspend fun writeRaw(frame: ByteArray) {
        writeMutex.withLock {
            raw?.write(frame)
        }
    }

    /** PROTO-18: one command record — LE u32 command id + payload. */
    suspend fun readCommand(): Pair<Long, ByteArray> {
        val buf = channel0.receive()
        if (buf.size < 4) throw XiaomiException("cs2: short command record")
        return buf.leU32(0) to buf.copyOfRange(4, buf.size)
    }

    /** PROTO-22: one media packet — 32-byte header + encrypted payload. */
    suspend fun readPacket(): Pair<ByteArray, ByteArray> {
        val buf = channel2.receive()
        if (buf.size < 32) throw XiaomiException("miss: packet header too small")
        return buf.copyOfRange(0, 32) to buf.copyOfRange(32, buf.size)
    }

    fun close() {
        closed = true
        channel0.close()
        channel2.close()
        try {
            raw?.close()
        } catch (_: Exception) {
        }
        scope.cancel()
    }
}
