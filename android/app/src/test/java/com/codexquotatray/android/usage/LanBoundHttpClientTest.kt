package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.LanAvailability
import com.codexquotatray.android.quota.LanSocketBinding
import okhttp3.OkHttpClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
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
}
