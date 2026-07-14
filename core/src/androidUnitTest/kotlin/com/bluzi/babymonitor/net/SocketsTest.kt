package com.bluzi.babymonitor.net

import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertThrows
import org.junit.Test

// WATCH-8: a blocking socket read cannot be interrupted by coroutine cancellation. If the real
// sockets ever go back to blocking forever, a camera that never answers parks the reconnect loop
// for good — the app sits on "Connecting…" all night and never finds the camera again. This is a
// property of the real java.net implementations, so it is tested against them, not a fake.

class SocketsTest {
    @Test(timeout = 15_000)
    fun `WATCH-8 a peer that never answers cannot park a UDP read forever`() {
        val udp = JavaSocketFactory.udp()
        try {
            assertThrows(TimeoutCancellationException::class.java) {
                runBlocking {
                    udp.bind() // bound to an ephemeral port nobody will ever send to
                    withTimeout(1_500) { udp.receive() }
                }
            }
        } finally {
            udp.close()
        }
    }

    @Test(timeout = 15_000)
    fun `WATCH-8 a UDP read gives up when the connection attempt is abandoned`() {
        val udp = JavaSocketFactory.udp()
        try {
            assertThrows(TimeoutCancellationException::class.java) {
                runBlocking {
                    udp.bind()
                    // The handshake budget is what must be able to expire — not just any timeout.
                    withTimeout(1_000) {
                        while (true) udp.receive()
                    }
                }
            }
        } finally {
            udp.close()
        }
    }
}
