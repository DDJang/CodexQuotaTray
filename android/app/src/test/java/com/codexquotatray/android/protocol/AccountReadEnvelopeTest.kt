package com.codexquotatray.android.protocol

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AccountReadEnvelopeTest {
    @Test
    fun accountReadCanRequestProactiveRefresh() {
        assertTrue(accountReadParams(refreshToken = true).refreshToken)
    }

    @Test
    fun accountReadCanKeepRefreshDisabledForInitialProbe() {
        assertFalse(accountReadParams(refreshToken = false).refreshToken)
    }
}
