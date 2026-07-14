package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.net.Datagram
import com.bluzi.babymonitor.net.SocketFactory
import com.bluzi.babymonitor.net.TcpSocket
import com.bluzi.babymonitor.net.UdpSocket
import kotlinx.coroutines.channels.Channel
import com.bluzi.babymonitor.runRealTimeTest
import kotlinx.coroutines.withTimeout
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.test.Test

private const val CAMERA = "192.168.1.50"

private class FakeUdp : UdpSocket {
    val incoming = Channel<Datagram>(Channel.UNLIMITED)
    val sent = mutableListOf<Triple<ByteArray, String, Int>>()
    var closed = false

    override suspend fun bind() {}
    override suspend fun send(data: ByteArray, host: String, port: Int) {
        sent.add(Triple(data, host, port))
    }

    override suspend fun receive(): Datagram = incoming.receive()
    override fun close() {
        closed = true
    }
}

private class FakeTcp : TcpSocket {
    val incoming = Channel<Byte>(Channel.UNLIMITED)
    val written = mutableListOf<ByteArray>()
    var connectedTo: Pair<String, Int>? = null

    fun feed(bytes: ByteArray) {
        for (b in bytes) incoming.trySend(b)
    }

    override suspend fun connect(host: String, port: Int) {
        connectedTo = host to port
    }

    override suspend fun write(data: ByteArray) {
        written.add(data)
    }

    override suspend fun readExact(n: Int): ByteArray = ByteArray(n) { incoming.receive() }
    override fun close() {}
}

private class FakeSockets(val fakeUdp: FakeUdp = FakeUdp(), val fakeTcp: FakeTcp = FakeTcp()) : SocketFactory {
    override fun udp(): UdpSocket = fakeUdp
    override fun tcp(): TcpSocket = fakeTcp
}

class Cs2ConnTest {
    private fun punchPacket() = byteArrayOf(0xf1.toByte(), 0x41, 0, 4, 9, 9, 9, 9)
    private fun readyTcp() = byteArrayOf(0xf1.toByte(), 0x43, 0, 0)
    private fun readyUdp() = byteArrayOf(0xf1.toByte(), 0x42, 0, 0)

    @Test
    fun `PROTO-15 handshake sends LAN search — echoes punch — then dials TCP on the announced port`() = runRealTimeTest {
        val sockets = FakeSockets()
        sockets.fakeUdp.incoming.trySend(Datagram(punchPacket(), CAMERA, 55555))
        sockets.fakeUdp.incoming.trySend(Datagram(readyTcp(), CAMERA, 55555))

        val conn = Cs2Conn(sockets)
        conn.dial(CAMERA, "tcp")

        // First thing on the wire: LAN search to the handshake port.
        val first = sockets.fakeUdp.sent.first()
        assertContentEquals(Cs2.LAN_SEARCH, first.first)
        assertEquals(CAMERA, first.second)
        assertEquals(Cs2.HANDSHAKE_PORT, first.third)

        // The punch packet is echoed back verbatim to the port it came from.
        assertTrue(sockets.fakeUdp.sent.any { it.first.contentEquals(punchPacket()) && it.third == 55555 })

        // TCP data phase on the announced port; handshake socket released.
        assertEquals(CAMERA to 55555, sockets.fakeTcp.connectedTo)
        assertTrue(sockets.fakeUdp.closed)
        assertTrue(conn.isTcp)
        conn.close()
    }

    @Test
    fun `PROTO-15 datagrams from other hosts are ignored during handshake`() = runRealTimeTest {
        val sockets = FakeSockets()
        sockets.fakeUdp.incoming.trySend(Datagram(punchPacket(), "10.0.0.99", 55555)) // impostor
        sockets.fakeUdp.incoming.trySend(Datagram(punchPacket(), CAMERA, 55555))
        sockets.fakeUdp.incoming.trySend(Datagram(readyTcp(), CAMERA, 55555))

        val conn = Cs2Conn(sockets)
        conn.dial(CAMERA, "tcp")
        assertEquals(CAMERA to 55555, sockets.fakeTcp.connectedTo)
        conn.close()
    }

    @Test
    fun `PROTO-16+18 tcp DRW payloads reassemble into command records across frame boundaries`() = runRealTimeTest {
        val sockets = FakeSockets()
        sockets.fakeUdp.incoming.trySend(Datagram(punchPacket(), CAMERA, 32108))
        sockets.fakeUdp.incoming.trySend(Datagram(readyTcp(), CAMERA, 32108))

        val conn = Cs2Conn(sockets)
        conn.dial(CAMERA, "tcp")

        // One command record: [BE u32 len][LE u32 cmd][payload], split across two DRW frames.
        val payload = "auth-ok".encodeToByteArray()
        val record = ByteArray(8 + payload.size)
        record.putBeU32(0, (4 + payload.size).toLong())
        record.putLeU32(4, 0x101)
        payload.copyInto(record, 8)

        // DRW frame: [0xF1 0xD0 size(BE16) 0xD1 ch seq(BE16)] + chunk
        fun drwFrame(chunk: ByteArray, seq: Int): ByteArray {
            val b = ByteArray(8 + chunk.size)
            b[0] = 0xf1.toByte()
            b[1] = 0xd0.toByte()
            b.putBeU16(2, 4 + 4 + chunk.size)
            b[4] = 0xd1.toByte()
            b[5] = 0
            b.putBeU16(6, seq)
            chunk.copyInto(b, 8)
            return b
        }

        val split = 5
        sockets.fakeTcp.feed(Cs2.tcpFrame(drwFrame(record.copyOfRange(0, split), 0)))
        sockets.fakeTcp.feed(Cs2.tcpFrame(drwFrame(record.copyOfRange(split, record.size), 1)))

        val (cmd, data) = withTimeout(2000) { conn.readCommand() }
        assertEquals(0x101L, cmd)
        assertContentEquals(payload, data)
        conn.close()
    }

    @Test
    fun `PROTO-17 udp data phase acks DRW packets and routes channels`() = runRealTimeTest {
        val sockets = FakeSockets()
        sockets.fakeUdp.incoming.trySend(Datagram(punchPacket(), CAMERA, 32108))
        sockets.fakeUdp.incoming.trySend(Datagram(readyUdp(), CAMERA, 32108))

        val conn = Cs2Conn(sockets)
        conn.dial(CAMERA, "udp")
        assertFalse(conn.isTcp)

        // One length-prefixed media record (PROTO-18): 32-byte header + 8 payload bytes.
        val record = ByteArray(4 + 40)
        record.putBeU32(0, 40)
        val frame = ByteArray(8 + record.size)
        frame[0] = 0xf1.toByte()
        frame[1] = 0xd0.toByte()
        frame.putBeU16(2, 4 + 4 + record.size)
        frame[4] = 0xd1.toByte()
        frame[5] = 2
        frame.putBeU16(6, 0x0304)
        record.copyInto(frame, 8)
        sockets.fakeUdp.incoming.trySend(Datagram(frame, CAMERA, 32108))

        val (hdr, body) = withTimeout(2000) { conn.readPacket() }
        assertEquals(32, hdr.size)
        assertEquals(8, body.size)

        // The DRW was acked with its channel + sequence.
        val expectedAck = Cs2.udpAck(2, 0x03, 0x04)
        assertTrue(sockets.fakeUdp.sent.any { it.first.contentEquals(expectedAck) })
        conn.close()
    }

    @Test
    fun `PROTO-18+22 media records spanning many DRW frames reassemble into one packet`() = runRealTimeTest {
        val sockets = FakeSockets()
        sockets.fakeUdp.incoming.trySend(Datagram(punchPacket(), CAMERA, 32108))
        sockets.fakeUdp.incoming.trySend(Datagram(readyTcp(), CAMERA, 32108))

        val conn = Cs2Conn(sockets)
        conn.dial(CAMERA, "tcp")

        // A keyframe-sized media record: 32-byte header + 3000 payload bytes,
        // length-prefixed and chopped into ~1KB DRW frames like the camera does.
        val inner = ByteArray(32 + 3000) { it.toByte() }
        val record = ByteArray(4 + inner.size)
        record.putBeU32(0, inner.size.toLong())
        inner.copyInto(record, 4)

        fun drwFrame(chunk: ByteArray, seq: Int): ByteArray {
            val b = ByteArray(8 + chunk.size)
            b[0] = 0xf1.toByte()
            b[1] = 0xd0.toByte()
            b.putBeU16(2, 4 + 4 + chunk.size)
            b[4] = 0xd1.toByte()
            b[5] = 2
            b.putBeU16(6, seq)
            chunk.copyInto(b, 8)
            return b
        }

        var seq = 0
        var off = 0
        while (off < record.size) {
            val end = minOf(off + 1024, record.size)
            sockets.fakeTcp.feed(Cs2.tcpFrame(drwFrame(record.copyOfRange(off, end), seq++)))
            off = end
        }

        val (hdr, body) = withTimeout(2000) { conn.readPacket() }
        assertContentEquals(inner.copyOfRange(0, 32), hdr)
        assertEquals(3000, body.size)
        assertContentEquals(inner.copyOfRange(32, inner.size), body)
        conn.close()
    }
}
