package com.codexquotatray.android

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class BottomDockGestureTest {
    @Test
    fun touchSlopSeparatesTapFromHorizontalDrag() {
        val slop = 12f

        assertFalse(isDockDrag(0f, slop))
        assertFalse(isDockDrag(slop - 0.01f, slop))
        assertFalse(isDockDrag(-slop, slop))
        assertTrue(isDockDrag(slop + 0.01f, slop))
        assertTrue(isDockDrag(-slop - 0.01f, slop))
    }
}
