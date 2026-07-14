package com.bluzi.babymonitor.net

// Socket seams for the CS2 transport — a real implementation per platform (java.net on Android,
// POSIX on Apple), scripted fakes in tests. Everything above this line is platform-free.
//
// Two properties every real implementation must have, and both are tested against the real
// sockets on every platform (SocketsTest), never against a fake:
//
//  - WATCH-8: a read must be interruptible. A camera that never answers must not park the
//    handshake forever — no timeout could fire and no further attempt would be made, and the app
//    would sit on "Connecting…" all night, having silently stopped looking.
//  - WATCH-7: a read must give up on its own. A camera that goes quiet must not hold the read
//    open until TCP's own timeout, which is tens of minutes away.

data class Datagram(val data: ByteArray, val host: String, val port: Int) {
    override fun equals(other: Any?): Boolean =
        other is Datagram && data.contentEquals(other.data) && host == other.host && port == other.port

    override fun hashCode(): Int = (data.contentHashCode() * 31 + host.hashCode()) * 31 + port
}

interface UdpSocket {
    suspend fun bind()
    suspend fun send(data: ByteArray, host: String, port: Int)
    suspend fun receive(): Datagram
    fun close()
}

interface TcpSocket {
    suspend fun connect(host: String, port: Int)
    suspend fun write(data: ByteArray)
    suspend fun readExact(n: Int): ByteArray
    fun close()
}

interface SocketFactory {
    fun udp(): UdpSocket
    fun tcp(): TcpSocket
}

/** The platform's real sockets. */
expect val platformSockets: SocketFactory

class XiaomiSocketClosed(message: String, cause: Throwable? = null) : Exception(message, cause)

internal const val CONNECT_TIMEOUT_MS = 5_000
internal const val READ_TIMEOUT_MS = 15_000
internal const val UDP_POLL_TIMEOUT_MS = 500
