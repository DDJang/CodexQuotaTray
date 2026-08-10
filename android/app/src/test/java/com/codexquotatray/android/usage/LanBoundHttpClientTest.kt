package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.LanAvailability
import okhttp3.OkHttpClient
import org.junit.Assert.assertSame
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

        assertSame(socketFactory, OkHttpClient().bindToWifiLan(provider).socketFactory)
    }
}
