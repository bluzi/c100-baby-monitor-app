package com.bluzi.babymonitor.xiaomi

import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.Test

// PROTO-24: the C100's night-shot values are 0=on, 1=off, 2=auto — note ON is 0, not 1.

class NightVisionModeTest {
    @Test
    fun `PROTO-24 modes map to their wire values`() {
        assertEquals(0, NightVisionMode.ON.value)
        assertEquals(1, NightVisionMode.OFF.value)
        assertEquals(2, NightVisionMode.AUTO.value)
    }

    @Test
    fun `PROTO-24 values round-trip back to modes`() {
        assertEquals(NightVisionMode.ON, NightVisionMode.fromValue(0))
        assertEquals(NightVisionMode.OFF, NightVisionMode.fromValue(1))
        assertEquals(NightVisionMode.AUTO, NightVisionMode.fromValue(2))
    }

    @Test
    fun `PROTO-24 an unknown value maps to null — not a crash`() {
        assertNull(NightVisionMode.fromValue(7))
    }
}
