package com.bluzi.babymonitor.net

import com.bluzi.babymonitor.platform.ioDispatcher
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.IntVar
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.alloc
import kotlinx.cinterop.allocPointerTo
import kotlinx.cinterop.convert
import kotlinx.cinterop.memScoped
import kotlinx.cinterop.pointed
import kotlinx.cinterop.ptr
import kotlinx.cinterop.reinterpret
import kotlinx.cinterop.sizeOf
import kotlinx.cinterop.toKString
import kotlinx.cinterop.usePinned
import kotlinx.cinterop.value
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import platform.posix.AF_INET
import platform.posix.EAGAIN
import platform.posix.EINPROGRESS
import platform.posix.EINTR
import platform.posix.EWOULDBLOCK
import platform.posix.F_GETFL
import platform.posix.F_SETFL
import platform.posix.IPPROTO_TCP
import platform.posix.O_NONBLOCK
import platform.posix.POLLOUT
import platform.posix.SOCK_DGRAM
import platform.posix.SOCK_STREAM
import platform.posix.SOL_SOCKET
import platform.posix.SO_ERROR
import platform.posix.SO_RCVTIMEO
import platform.posix.TCP_NODELAY
import platform.posix.addrinfo
import platform.posix.close
import platform.posix.connect
import platform.posix.errno
import platform.posix.fcntl
import platform.posix.freeaddrinfo
import platform.posix.gai_strerror
import platform.posix.getaddrinfo
import platform.posix.getsockopt
import platform.posix.poll
import platform.posix.pollfd
import platform.posix.recv
import platform.posix.recvfrom
import platform.posix.send
import platform.posix.sendto
import platform.posix.setsockopt
import platform.posix.sockaddr
import platform.posix.sockaddr_in
import platform.posix.socket
import platform.posix.socklen_tVar
import platform.posix.strerror
import platform.posix.timeval

// Apple's sockets, straight on POSIX.
//
// Why not a networking library: the CS2 transport needs exactly this much and no more — a UDP
// socket that can be told to give up on a read, and a TCP socket with a read timeout. The two
// properties that matter are WATCH-7 and WATCH-8, and both are properties of the syscalls:
//
//  - SO_RCVTIMEO is what makes a read give up. Without it a camera that goes quiet holds the read
//    open until TCP's own timeout — tens of minutes — and the app shows "Live" the whole time.
//  - The UDP receive polls in a loop with a short timeout instead of blocking forever, so
//    coroutine cancellation and withTimeout can take effect. Without it, a camera that never
//    answers parks the handshake for good and the app sits on "Connecting…" all night.
//
// Both are asserted against these real sockets in SocketsTest — the same test the JVM sockets
// pass, run again here.

@OptIn(ExperimentalForeignApi::class)
actual val platformSockets: SocketFactory get() = PosixSocketFactory

object PosixSocketFactory : SocketFactory {
    override fun udp(): UdpSocket = PosixUdpSocket()
    override fun tcp(): TcpSocket = PosixTcpSocket()
}

@OptIn(ExperimentalForeignApi::class)
private fun posixError(what: String): Nothing {
    val code = errno
    throw XiaomiSocketClosed("$what: ${strerror(code)?.toKString() ?: "errno $code"}")
}

@OptIn(ExperimentalForeignApi::class)
private class PosixTcpSocket : TcpSocket {
    private var fd: Int = -1

    override suspend fun connect(host: String, port: Int): Unit = withContext(ioDispatcher) {
        memScoped {
            val hints = alloc<addrinfo>()
            hints.ai_family = AF_INET
            hints.ai_socktype = SOCK_STREAM
            val result = allocPointerTo<addrinfo>()
            val rc = getaddrinfo(host, port.toString(), hints.ptr, result.ptr)
            if (rc != 0) {
                throw XiaomiSocketClosed("tcp: cannot resolve $host: ${gai_strerror(rc)?.toKString()}")
            }
            val info = result.value ?: throw XiaomiSocketClosed("tcp: cannot resolve $host")
            try {
                val s = socket(AF_INET, SOCK_STREAM, 0)
                if (s < 0) posixError("tcp: socket")

                // Non-blocking connect + poll, so the 5 s budget is real. A blocking connect would
                // wait out the kernel's own timeout — over a minute — and hold the reconnect loop
                // with it, long past the point where we should have tried again.
                val flags = fcntl(s, F_GETFL, 0)
                fcntl(s, F_SETFL, flags or O_NONBLOCK)
                if (connect(s, info.pointed.ai_addr, info.pointed.ai_addrlen) != 0 && errno != EINPROGRESS) {
                    close(s)
                    posixError("tcp: connect")
                }
                val pfd = alloc<pollfd>()
                pfd.fd = s
                pfd.events = POLLOUT.toShort()
                if (poll(pfd.ptr, 1.convert(), CONNECT_TIMEOUT_MS) <= 0) {
                    close(s)
                    throw XiaomiSocketClosed("tcp: connect to $host:$port timed out")
                }
                val soError = alloc<IntVar>()
                val soLen = alloc<socklen_tVar>()
                soLen.value = sizeOf<IntVar>().convert()
                getsockopt(s, SOL_SOCKET, SO_ERROR, soError.ptr, soLen.ptr)
                if (soError.value != 0) {
                    close(s)
                    throw XiaomiSocketClosed(
                        "tcp: connect to $host:$port failed: ${strerror(soError.value)?.toKString()}",
                    )
                }
                fcntl(s, F_SETFL, flags) // back to blocking; the read timeout does the rest

                // WATCH-7: a camera that goes quiet must not hold the read open until TCP gives up.
                // The camera pings us every second, so this much silence means it is gone.
                val tv = alloc<timeval>()
                tv.tv_sec = (READ_TIMEOUT_MS / 1000).convert()
                tv.tv_usec = 0.convert()
                setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, tv.ptr, sizeOf<timeval>().convert())

                val one = alloc<IntVar>()
                one.value = 1
                setsockopt(s, IPPROTO_TCP, TCP_NODELAY, one.ptr, sizeOf<IntVar>().convert())

                fd = s
            } finally {
                freeaddrinfo(result.value)
            }
        }
    }

    override suspend fun write(data: ByteArray): Unit = withContext(ioDispatcher) {
        val s = fd
        if (s < 0) throw IllegalStateException("tcp: not connected")
        data.usePinned { pinned ->
            var off = 0
            while (off < data.size) {
                val sent = send(s, pinned.addressOf(off), (data.size - off).convert(), 0)
                if (sent <= 0) {
                    if (errno == EINTR) continue
                    posixError("tcp: send")
                }
                off += sent.toInt()
            }
        }
    }

    override suspend fun readExact(n: Int): ByteArray = withContext(ioDispatcher) {
        val s = fd
        if (s < 0) throw IllegalStateException("tcp: not connected")
        val buf = ByteArray(n)
        if (n == 0) return@withContext buf
        buf.usePinned { pinned ->
            var off = 0
            while (off < n) {
                val got = recv(s, pinned.addressOf(off), (n - off).convert(), 0)
                when {
                    got > 0 -> off += got.toInt()
                    got == 0L -> throw XiaomiSocketClosed("tcp: connection closed")
                    errno == EINTR -> Unit // interrupted before any data — just retry
                    else -> posixError("tcp: recv")
                }
            }
        }
        buf
    }

    override fun close() {
        val s = fd
        fd = -1
        if (s >= 0) close(s)
    }
}

@OptIn(ExperimentalForeignApi::class)
private class PosixUdpSocket : UdpSocket {
    private var fd: Int = -1

    override suspend fun bind(): Unit = withContext(ioDispatcher) {
        val s = socket(AF_INET, SOCK_DGRAM, 0)
        if (s < 0) posixError("udp: socket")
        memScoped {
            // WATCH-8: poll rather than block indefinitely. A blocking recvfrom cannot be
            // interrupted by coroutine cancellation, so without this a camera that never answers
            // would park the handshake forever — no timeout could fire, and no further attempt
            // would ever be made.
            val tv = alloc<timeval>()
            tv.tv_sec = 0.convert()
            tv.tv_usec = (UDP_POLL_TIMEOUT_MS * 1000).convert()
            setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, tv.ptr, sizeOf<timeval>().convert())
        }
        // Deliberately not bound: the first send assigns an ephemeral port, exactly as
        // java.net.DatagramSocket() does.
        fd = s
    }

    override suspend fun send(data: ByteArray, host: String, port: Int): Unit = withContext(ioDispatcher) {
        val s = fd
        if (s < 0) throw IllegalStateException("udp: not bound")
        memScoped {
            val addr = alloc<sockaddr_in>()
            addr.sin_family = AF_INET.convert()
            addr.sin_port = hostToNetworkShort(port)
            addr.sin_addr.s_addr = parseIpv4(host)
            data.usePinned { pinned ->
                val sent = sendto(
                    s,
                    pinned.addressOf(0),
                    data.size.convert(),
                    0,
                    addr.ptr.reinterpret<sockaddr>(),
                    sizeOf<sockaddr_in>().convert(),
                )
                if (sent < 0) posixError("udp: sendto")
            }
        }
    }

    override suspend fun receive(): Datagram = withContext(ioDispatcher) {
        val s = fd
        if (s < 0) throw IllegalStateException("udp: not bound")
        val buf = ByteArray(2048)
        while (true) {
            ensureActive() // the poll gap is where cancellation and withTimeout get their chance
            val datagram = memScoped {
                val from = alloc<sockaddr_in>()
                val fromLen = alloc<socklen_tVar>()
                fromLen.value = sizeOf<sockaddr_in>().convert()
                val got = buf.usePinned { pinned ->
                    recvfrom(
                        s,
                        pinned.addressOf(0),
                        buf.size.convert(),
                        0,
                        from.ptr.reinterpret<sockaddr>(),
                        fromLen.ptr,
                    )
                }
                when {
                    got > 0 -> Datagram(
                        buf.copyOf(got.toInt()),
                        formatIpv4(from.sin_addr.s_addr),
                        networkToHostShort(from.sin_port),
                    )
                    got < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) -> null
                    else -> posixError("udp: recvfrom")
                }
            }
            if (datagram != null) return@withContext datagram
        }
        @Suppress("UNREACHABLE_CODE")
        throw IllegalStateException("udp: unreachable")
    }

    override fun close() {
        val s = fd
        fd = -1
        if (s >= 0) close(s)
    }
}

// htons/ntohs/inet_pton/inet_ntop are macros or absent from Darwin's cinterop bindings, so the
// two conversions the monitor needs are done here. Both Apple targets are little-endian, so
// "network order" means the bytes in an s_addr read a.b.c.d from the low byte up.

private fun hostToNetworkShort(port: Int): UShort =
    (((port and 0xff) shl 8) or ((port shr 8) and 0xff)).toUShort()

private fun networkToHostShort(port: UShort): Int {
    val v = port.toInt()
    return ((v and 0xff) shl 8) or ((v shr 8) and 0xff)
}

/** "192.168.1.20" → network-order s_addr. The camera's address always arrives as a literal. */
internal fun parseIpv4(host: String): UInt {
    val parts = host.split('.')
    require(parts.size == 4) { "expected an IPv4 address, got '$host'" }
    var result = 0u
    for (i in 3 downTo 0) {
        val octet = parts[i].toUIntOrNull()
        require(octet != null && octet <= 255u) { "bad IPv4 address '$host'" }
        result = (result shl 8) or octet
    }
    return result // parts[3] ended up in the low byte, which is network order on little-endian
}

internal fun formatIpv4(addr: UInt): String {
    val a = addr and 0xffu
    val b = (addr shr 8) and 0xffu
    val c = (addr shr 16) and 0xffu
    val d = (addr shr 24) and 0xffu
    return "$a.$b.$c.$d"
}
