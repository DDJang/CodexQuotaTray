package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.LanAvailability
import com.codexquotatray.android.quota.LanSocketBinding
import okhttp3.OkHttpClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import javax.net.SocketFactory

class LanBoundHttpClientTest {
    @Test
    fun lanRequestsUseTheSelectedWifiSocketFactoryWhenAvailable() {
        val socketFactory = SocketFactory.getDefault()
        val provider = object : LanAvailability {
            override fun isAvailable() = true
            override fun socketFactoryOrNull(): SocketFactory = socketFactory
        }

        assertSame(socketFactory, OkHttpClient().bindToWifiLan(provider, "192.168.1.10").socketFactory)
    }

    @Test
    fun lanBindingDiagnosticsExposeTheSelectedNetworkHandleWithoutSecrets() {
        val socketFactory = SocketFactory.getDefault()
        val messages = mutableListOf<String>()
        val provider = object : LanAvailability {
            override fun isAvailable() = true
            override fun socketBindingForHostOrNull(host: String): LanSocketBinding =
                LanSocketBinding(socketFactory, "wifi-handle")
        }

        OkHttpClient().bindToWifiLan(provider, "192.168.1.10") { message -> messages += message }

        assertEquals(listOf("LAN bound network=wifi-handle"), messages)
    }

    @Test fun missingHostRouteFailsClosedInsteadOfFallingBackToCellularOrVpn() {
        val provider = object : LanAvailability {
            override fun isAvailable(): Boolean = true
        }
        assertThrows(java.io.IOException::class.java) {
            OkHttpClient().bindToWifiLan(provider, "192.168.1.10")
        }
    }

    @Test fun bindingFromAnExpiredGenerationIsRejectedBeforeSocketUse() {
        LanNetworkEpoch.resetForTest(2L)
        val socketFactory = SocketFactory.getDefault()
        val provider = object : LanAvailability {
            override fun isAvailable() = true
            override fun socketBindingForHostOrNull(host: String): LanSocketBinding =
                LanSocketBinding(socketFactory, "old-network", networkGeneration = 1L)
        }
        val attempt = LanAttemptContext(
            channel = "token",
            id = 1L,
            diagnostics = NoOpLanDiagnosticLogger,
            networkGeneration = 1L,
        )

        assertThrows(LanAttemptStaleException::class.java) {
            OkHttpClient().bindToWifiLan(provider, "192.168.1.10", diagnostics = null, attempt = attempt)
        }
        LanNetworkEpoch.resetForTest()
    }

    @Test fun correlatedBindingRecordsTheExactNetworkAndGeneration() {
        LanNetworkEpoch.resetForTest(9L)
        val messages = mutableListOf<String>()
        val socketFactory = SocketFactory.getDefault()
        val provider = object : LanAvailability {
            override fun isAvailable() = true
            override fun socketBindingForHostOrNull(host: String) =
                LanSocketBinding(socketFactory, "wifi-42", networkGeneration = 9L)
        }
        val attempt = LanAttemptContext("token", 7L, LanDiagnosticLogger(messages::add), networkGeneration = 9L)

        val bound = OkHttpClient().bindToWifiLan(provider, "192.168.1.10", null, attempt)

        assertSame(socketFactory, bound.socketFactory)
        assertTrue(messages.single { it.contains("socketBinding") }.contains("boundToNetwork=true"))
        assertTrue(messages.single { it.contains("socketBinding") }.contains("networkHandle=wifi-42"))
        assertTrue(messages.single { it.contains("socketBinding") }.contains("bindingGeneration=9"))
        LanNetworkEpoch.resetForTest()
    }
}
