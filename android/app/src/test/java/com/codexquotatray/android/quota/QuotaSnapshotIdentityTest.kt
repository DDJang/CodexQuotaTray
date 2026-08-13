package com.codexquotatray.android.quota

import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.cacheIdentity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaSnapshotIdentityTest {
    @Test
    fun replacingWindowsPairingDoesNotRestoreTheOldWindowsSnapshot() {
        assertFalse(shouldRestoreQuotaSnapshot(QuotaSource.WINDOWS, "device-a", "device-b"))
        assertFalse(shouldRestoreQuotaSnapshot(QuotaSource.WINDOWS, "device-a", null))
        assertTrue(shouldInvalidateWindowsQuotaSnapshot(QuotaSource.WINDOWS, "device-a", "device-b"))
        assertTrue(shouldInvalidateWindowsQuotaSnapshot(QuotaSource.WINDOWS, "device-a", null))
    }

    @Test
    fun directSnapshotSurvivesWindowsPairingReplacement() {
        assertTrue(shouldRestoreQuotaSnapshot(QuotaSource.DIRECT, null, "device-b"))
        assertFalse(shouldInvalidateWindowsQuotaSnapshot(QuotaSource.DIRECT, null, "device-b"))
    }

    @Test
    fun hostRelocationForTheSameDeviceKeepsTheWindowsSnapshot() {
        val original = pairing("192.168.1.10")
        val relocated = pairing("192.168.1.11")

        assertEquals(original.cacheIdentity(), relocated.cacheIdentity())
        assertTrue(shouldRestoreQuotaSnapshot(QuotaSource.WINDOWS, original.cacheIdentity(), relocated.cacheIdentity()))
        assertFalse(shouldInvalidateWindowsQuotaSnapshot(QuotaSource.WINDOWS, original.cacheIdentity(), relocated.cacheIdentity()))
    }

    @Test
    fun missingWindowsIdentityFailsClosed() {
        assertFalse(shouldRestoreQuotaSnapshot(QuotaSource.WINDOWS, null, "device-a"))
    }

    private fun pairing(host: String) = TokenSyncPairing(
        deviceId = "123e4567-e89b-12d3-a456-426614174000",
        pairingSecret = "test-secret",
        lastKnownHost = host,
        port = 43821,
    )
}
