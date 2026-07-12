package com.bluzi.babymonitor.xiaomi

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class Cs2FramingTest {
    @Test
    fun `PROTO-17 command frames carry BE sizes, channel, seq, and LE-free layout`() {
        val payload = byteArrayOf(1, 2, 3)
        val frame = Cs2.marshalCmd(channel = 0, seq = 0x0102, cmd = 0x100, payload = payload)
        assertEquals(0xf1, frame[0].toInt() and 0xff)
        assertEquals(0xd0, frame[1].toInt() and 0xff)
        assertEquals(12 + payload.size, frame.beU16(2)) // body size
        assertEquals(0xd1, frame[4].toInt() and 0xff)
        assertEquals(0, frame[5].toInt())
        assertEquals(0x0102, frame.beU16(6))
        assertEquals((4 + payload.size).toLong(), frame.beU32(8)) // record length prefix
        assertEquals(0x100L, frame.beU32(12))
        assertArrayEquals(payload, frame.copyOfRange(16, frame.size))
    }

    @Test
    fun `PROTO-16 tcp frames have BE u16 size and 0x68 magic in an 8-byte header`() {
        val body = byteArrayOf(9, 8, 7, 6, 5)
        val framed = Cs2.tcpFrame(body)
        assertEquals(8 + body.size, framed.size)
        assertEquals(body.size, framed.beU16(0))
        assertEquals(0x68, framed[2].toInt() and 0xff)
        assertArrayEquals(body, framed.copyOfRange(8, framed.size))
    }

    @Test
    fun `PROTO-17 udp acks echo channel and sequence`() {
        val ack = Cs2.udpAck(2, 0x01, 0x02)
        assertArrayEquals(
            byteArrayOf(0xf1.toByte(), 0xd1.toByte(), 0, 6, 0xd1.toByte(), 2, 0, 1, 0x01, 0x02),
            ack,
        )
    }

    @Test
    fun `PROTO-18 records reassemble regardless of chunk boundaries`() {
        val asm = RecordAssembler()
        val record = "hello miss".toByteArray()
        val stream = ByteArray(4 + record.size)
        stream.putBeU32(0, record.size.toLong())
        record.copyInto(stream, 4)

        // Split awkwardly: 3 bytes (inside the length prefix), then the rest.
        assertTrue(asm.push(stream.copyOfRange(0, 3)).isEmpty())
        val out = asm.push(stream.copyOfRange(3, stream.size))
        assertEquals(1, out.size)
        assertArrayEquals(record, out[0])
    }

    @Test
    fun `PROTO-18 a corrupt record length is a dead connection, not a crash`() {
        // A length prefix ≥ 2^31 would turn into a negative Int and crash copyOfRange —
        // taking the whole process down. It must surface as a protocol error instead.
        val asm = RecordAssembler()
        val corrupt = ByteArray(8)
        corrupt.putBeU32(0, 0xFFFFFFFFL)
        val err = assertThrows(XiaomiException::class.java) { asm.push(corrupt) }
        assertTrue(err.message!!.contains("corrupt record length"))

        // A merely-huge (positive) length is equally impossible for a real record.
        val huge = ByteArray(8)
        huge.putBeU32(0, 0x40000000L) // 1 GiB
        assertThrows(XiaomiException::class.java) { RecordAssembler().push(huge) }
    }

    @Test
    fun `PROTO-18 multiple records in one chunk all come out`() {
        val asm = RecordAssembler()
        val recA = byteArrayOf(1, 1, 1, 1, 1)
        val recB = byteArrayOf(2, 2)
        val stream = ByteArray(4 + recA.size + 4 + recB.size)
        stream.putBeU32(0, recA.size.toLong())
        recA.copyInto(stream, 4)
        stream.putBeU32(4 + recA.size, recB.size.toLong())
        recB.copyInto(stream, 8 + recA.size)

        val out = asm.push(stream)
        assertEquals(2, out.size)
        assertArrayEquals(recA, out[0])
        assertArrayEquals(recB, out[1])
    }
}
