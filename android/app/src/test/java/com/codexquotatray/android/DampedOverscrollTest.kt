package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

class DampedOverscrollTest {
    @Test
    fun `damped displacement has no hard limit`() {
        val resistanceDistance = 180f
        val near = dampedOverscrollDisplacement(180f, resistanceDistance)
        val far = dampedOverscrollDisplacement(1_800f, resistanceDistance)
        val farther = dampedOverscrollDisplacement(18_000f, resistanceDistance)

        assertTrue(far > near)
        assertTrue(farther > far)
    }

    @Test
    fun `damped displacement progressively resists added drag`() {
        val resistanceDistance = 180f
        val first = dampedOverscrollDisplacement(1_000f, resistanceDistance)
        val second = dampedOverscrollDisplacement(2_000f, resistanceDistance)

        assertTrue(second - first < 1_000f)
        assertEquals(-first, dampedOverscrollDisplacement(-1_000f, resistanceDistance), 0.001f)
        assertEquals(0f, dampedOverscrollDisplacement(0f, resistanceDistance), 0f)
        assertTrue(abs(first) > 0f)
    }

    @Test
    fun `rebound starts for drag or remaining fling velocity`() {
        assertTrue(shouldStartOverscrollRebound(12f, 0f))
        assertTrue(shouldStartOverscrollRebound(0f, 240f))
        assertTrue(shouldStartOverscrollRebound(-12f, -240f))
        assertTrue(!shouldStartOverscrollRebound(0f, 0f))
    }
}
