package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MainPageSwitcherTest {
    @Test
    fun firstPageHasNoEnterAnimation() {
        val transition = mainPageEnterTransition(previousIndex = null, selectedIndex = 0)
        assertFalse(transition.shouldAnimate)
        assertEquals(0, transition.direction)
    }

    @Test
    fun quotaToTokenEntersFromRight() {
        val transition = mainPageEnterTransition(previousIndex = 0, selectedIndex = 1)
        assertTrue(transition.shouldAnimate)
        assertEquals(1, transition.direction)
    }

    @Test
    fun tokenToQuotaEntersFromLeft() {
        val transition = mainPageEnterTransition(previousIndex = 1, selectedIndex = 0)
        assertTrue(transition.shouldAnimate)
        assertEquals(-1, transition.direction)
    }

    @Test
    fun repeatedExternalSelectionDoesNotAnimate() {
        val transition = mainPageEnterTransition(previousIndex = 1, selectedIndex = 1)
        assertFalse(transition.shouldAnimate)
        assertEquals(0, transition.direction)
    }
}
