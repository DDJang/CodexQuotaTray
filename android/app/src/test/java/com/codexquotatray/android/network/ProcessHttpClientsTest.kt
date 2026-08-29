package com.codexquotatray.android.network

import com.codexquotatray.android.auth.CodexOAuthClient
import com.codexquotatray.android.usage.CodexUsageClient
import com.codexquotatray.android.usage.TokenUsageSyncClient
import com.codexquotatray.android.usage.WindowsQuotaFallbackClient
import org.junit.Assert.assertFalse
import org.junit.Assert.assertSame
import org.junit.Test

class ProcessHttpClientsTest {
    @Test
    fun openAIClientsShareProcessPoolAndDispatcherWhileKeepingTheirTimeoutProfiles() {
        val oauth = CodexOAuthClient.defaultClient()
        val usage = CodexUsageClient.defaultClient()

        assertSame(oauth.connectionPool, usage.connectionPool)
        assertSame(oauth.dispatcher, usage.dispatcher)
        assertFalse(oauth.callTimeoutMillis == usage.callTimeoutMillis)
    }

    @Test
    fun lanClientsShareProcessPoolAndKeepRedirectsDisabled() {
        val token = TokenUsageSyncClient.defaultClient()
        val quota = WindowsQuotaFallbackClient.defaultClient()

        assertSame(token.connectionPool, quota.connectionPool)
        assertSame(token.dispatcher, quota.dispatcher)
        assertFalse(token.followRedirects)
        assertFalse(token.followSslRedirects)
        assertFalse(quota.followRedirects)
        assertFalse(quota.followSslRedirects)
        assertFalse(token.callTimeoutMillis == quota.callTimeoutMillis)
    }
}
