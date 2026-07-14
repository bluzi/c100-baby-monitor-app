package com.bluzi.babymonitor.net

import com.bluzi.babymonitor.runRealTimeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout

// WATCH-8: a blocking socket read cannot be interrupted by coroutine cancellation. If the real
// sockets ever go back to blocking forever, a camera that never answers parks the reconnect loop
// for good — the app sits on "Connecting…" all night and never looks for the camera again.
//
// This is a property of the real POSIX implementation, so it is tested against it, not a fake —
// the same test the JVM sockets pass. Every platform that ships this app owes the same promise.

class SocketsTest {
    @Test
    fun `WATCH-8 a peer that never answers cannot park a UDP read forever`() = runRealTimeTest {
        val udp = PosixSocketFactory.udp()
        try {
            assertFailsWith<TimeoutCancellationException> {
                udp.bind() // an ephemeral port nobody will ever send to
                withTimeout(1_500) { udp.receive() }
            }
        } finally {
            udp.close()
        }
    }

    @Test
    fun `WATCH-8 a UDP read gives up when the connection attempt is abandoned`() = runRealTimeTest {
        val udp = PosixSocketFactory.udp()
        try {
            assertFailsWith<TimeoutCancellationException> {
                udp.bind()
                // The handshake budget is what must be able to expire — not just any timeout.
                withTimeout(1_000) {
                    while (true) udp.receive()
                }
            }
        } finally {
            udp.close()
        }
    }

    @Test
    fun `WATCH-7 a TCP connect to a black hole gives up rather than hanging on the kernel`() =
        runRealTimeTest {
            val tcp = PosixSocketFactory.tcp()
            try {
                // TEST-NET-1 (RFC 5737): routable-looking, never answers. A blocking connect would
                // sit here for over a minute; ours must give up inside its own budget.
                assertFailsWith<XiaomiSocketClosed> {
                    withTimeout(8_000) { tcp.connect("192.0.2.1", 32108) }
                }
            } finally {
                tcp.close()
            }
        }

    @Test
    fun `the IPv4 helpers round-trip an address`() {
        assertEquals("192.168.1.50", formatIpv4(parseIpv4("192.168.1.50")))
        assertEquals("10.0.0.1", formatIpv4(parseIpv4("10.0.0.1")))
        assertEquals("255.255.255.255", formatIpv4(parseIpv4("255.255.255.255")))
    }
}
