package com.codexquotatray.android

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LiquidModalOverlayTest {
    @Test
    fun scrimDismissPolicyOnlyDismissesAllowedTapCandidates() {
        assertTrue(shouldDismissModalScrimTap(dismissOnClickOutside = true, tapCandidate = true))
        assertFalse(shouldDismissModalScrimTap(dismissOnClickOutside = false, tapCandidate = true))
        assertFalse(shouldDismissModalScrimTap(dismissOnClickOutside = true, tapCandidate = false))
    }
}
