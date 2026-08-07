package com.codexquotatray.android.poc

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class P0_5RecoveryTest {
    @Test
    fun transportFailuresAreRecoverableOnce() {
        assertTrue(isRecoverableServerFailure("connection_refused"))
        assertTrue(isRecoverableServerFailure("websocket_open_failed"))
        assertTrue(isRecoverableServerFailure("initialize_timeout"))
    }

    @Test
    fun protocolAndAuthenticationFailuresAreNotRetried() {
        assertFalse(isRecoverableServerFailure("rate_limits_rpc_error"))
        assertFalse(isRecoverableServerFailure("unauthenticated"))
        assertFalse(isRecoverableServerFailure("initialize_protocol_error"))
    }
}
