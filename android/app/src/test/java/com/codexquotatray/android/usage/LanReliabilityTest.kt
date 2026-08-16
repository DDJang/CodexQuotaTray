package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.Ipv4Route
import com.codexquotatray.android.quota.LanNetworkCandidate
import com.codexquotatray.android.quota.LanNetworkSelector
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LanReliabilityTest {
    @Test fun discoveryMulticastLockIsAcquiredAndReleasedOnSuccess() {
        val lock = FakeLock()
        val result = withDiscoveryMulticastLock(lock) {
            assertTrue(lock.held)
            "found"
        }
        assertEquals("found", result)
        assertEquals(1, lock.acquireCalls)
        assertEquals(1, lock.releaseCalls)
        assertFalse(lock.held)
    }

    @Test fun discoveryMulticastLockIsReleasedWhenDiscoveryFails() {
        val lock = FakeLock()
        runCatching { withDiscoveryMulticastLock(lock) { error("NSD failed") } }
        assertEquals(1, lock.acquireCalls)
        assertEquals(1, lock.releaseCalls)
        assertFalse(lock.held)
    }

    @Test fun failedMulticastAcquisitionDoesNotPretendToHoldOrReleaseLock() {
        val lock = FakeLock(failAcquire = true)
        assertTrue(runCatching { withDiscoveryMulticastLock(lock) { Unit } }.isFailure)
        assertEquals(1, lock.acquireCalls)
        assertEquals(0, lock.releaseCalls)
    }

    @Test fun routeSelectionChoosesWifiWithMostSpecificRouteToPairedHost() {
        val candidates = listOf(
            candidate("unrelated", route("10.0.0.0", 8)),
            candidate("default", route("0.0.0.0", 0)),
            candidate("paired-lan", route("192.168.50.0", 24)),
        )
        assertEquals("paired-lan", LanNetworkSelector.select(candidates, "192.168.50.42")?.value)
    }

    @Test fun routeSelectionFailsClosedWhenNoWifiRouteMatches() {
        val candidates = listOf(candidate("other", route("10.0.0.0", 8)))
        assertNull(LanNetworkSelector.select(candidates, "192.168.50.42"))
    }

    private fun candidate(name: String, vararg routes: Ipv4Route) =
        LanNetworkCandidate(name, name, routes.toList())

    private fun route(address: String, prefix: Int) = Ipv4Route(
        address.split('.').map { it.toInt().toByte() }.toByteArray(),
        prefix,
    )

    private class FakeLock(private val failAcquire: Boolean = false) : DiscoveryMulticastLock {
        var held = false
        var acquireCalls = 0
        var releaseCalls = 0
        override fun acquire() {
            acquireCalls++
            if (failAcquire) error("permission denied")
            held = true
        }
        override fun release() {
            releaseCalls++
            held = false
        }
    }
}
