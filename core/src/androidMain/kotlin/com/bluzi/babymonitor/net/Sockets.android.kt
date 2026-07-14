package com.bluzi.babymonitor.net

import com.bluzi.babymonitor.platform.ioDispatcher
import java.io.DataInputStream
import java.io.EOFException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

actual val platformSockets: SocketFactory get() = JavaSocketFactory

object JavaSocketFactory : SocketFactory {
    override fun udp(): UdpSocket = JavaUdpSocket()
    override fun tcp(): TcpSocket = JavaTcpSocket()
}

private class JavaUdpSocket : UdpSocket {
    private var socket: DatagramSocket? = null

    override suspend fun bind() = withContext(ioDispatcher) {
        // WATCH-8: poll rather than block indefinitely. A raw DatagramSocket.receive() cannot be
        // interrupted by coroutine cancellation, so without this a camera that never answers would
        // park the handshake forever — no timeout could fire and no further attempt would be made.
        socket = DatagramSocket().apply { soTimeout = UDP_POLL_TIMEOUT_MS }
    }

    override suspend fun send(data: ByteArray, host: String, port: Int) = withContext(ioDispatcher) {
        val s = socket ?: throw IllegalStateException("udp: not bound")
        s.send(DatagramPacket(data, data.size, InetAddress.getByName(host), port))
    }

    override suspend fun receive(): Datagram = withContext(ioDispatcher) {
        val s = socket ?: throw IllegalStateException("udp: not bound")
        val buf = ByteArray(2048)
        while (true) {
            ensureActive() // the poll gap is where cancellation and withTimeout get their chance
            val pkt = DatagramPacket(buf, buf.size)
            try {
                s.receive(pkt)
                return@withContext Datagram(buf.copyOf(pkt.length), pkt.address.hostAddress ?: "", pkt.port)
            } catch (_: SocketTimeoutException) {
                // Nothing yet — go round again so we stay interruptible.
            }
        }
        @Suppress("UNREACHABLE_CODE")
        throw IllegalStateException("udp: unreachable")
    }

    override fun close() {
        socket?.close()
    }
}

private class JavaTcpSocket : TcpSocket {
    private var socket: Socket? = null
    private var input: DataInputStream? = null

    override suspend fun connect(host: String, port: Int) = withContext(ioDispatcher) {
        val s = Socket()
        s.tcpNoDelay = true
        s.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        // WATCH-7: a camera that goes quiet must not hold the read open until TCP gives up (tens
        // of minutes). The camera pings us every second, so this much silence means it is gone.
        s.soTimeout = READ_TIMEOUT_MS
        input = DataInputStream(s.getInputStream())
        socket = s
    }

    override suspend fun write(data: ByteArray) = withContext(ioDispatcher) {
        val s = socket ?: throw IllegalStateException("tcp: not connected")
        s.getOutputStream().let {
            it.write(data)
            it.flush()
        }
    }

    override suspend fun readExact(n: Int): ByteArray = withContext(ioDispatcher) {
        val i = input ?: throw IllegalStateException("tcp: not connected")
        val buf = ByteArray(n)
        try {
            i.readFully(buf)
        } catch (e: EOFException) {
            throw XiaomiSocketClosed("tcp: connection closed", e)
        }
        buf
    }

    override fun close() {
        try {
            socket?.close()
        } catch (_: Exception) {
        }
    }
}
