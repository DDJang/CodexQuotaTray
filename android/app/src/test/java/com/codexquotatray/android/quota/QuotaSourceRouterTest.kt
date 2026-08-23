package com.codexquotatray.android.quota

import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.source.DataSourcePriority
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class QuotaSourceRouterTest {
    private val router = QuotaSourceRouter()

    @Test fun openAIFirstUsesOpenAISuccess() = assertRoute(DataSourcePriority.OPENAI_FIRST, "openai", false, "openai")
    @Test fun openAIFirstFallsBackToWindows() = assertRoute(DataSourcePriority.OPENAI_FIRST, "openai", true, "windows")
    @Test fun windowsFirstUsesWindowsSuccess() = assertRoute(DataSourcePriority.WINDOWS_FIRST, "windows", false, "windows")
    @Test fun windowsFirstFallsBackToOpenAI() = assertRoute(DataSourcePriority.WINDOWS_FIRST, "windows", true, "openai")

    @Test fun unavailablePreferredProviderIsSkipped() {
        val result = router.read(
            DataSourcePriority.OPENAI_FIRST,
            hasOpenAI = false,
            hasWindows = true,
            openAI = { error("must not run") },
            windows = { read("windows") },
        )
        assertEquals("windows", result.quota.planType)
    }

    @Test fun bothUnavailableFails() {
        assertThrows(QuotaReadException::class.java) {
            router.read(DataSourcePriority.OPENAI_FIRST, false, false, { read("openai") }, { read("windows") })
        }
    }

    private fun assertRoute(priority: DataSourcePriority, preferred: String, failPreferred: Boolean, expected: String) {
        val result = router.read(
            priority,
            true,
            true,
            openAI = { if (failPreferred && preferred == "openai") throw failure() else read("openai") },
            windows = { if (failPreferred && preferred == "windows") throw failure() else read("windows") },
        )
        assertEquals(expected, result.quota.planType)
    }

    private fun read(name: String) = QuotaSourceRead(DirectQuotaResult(name, emptyList(), "available", 1L))
    private fun failure() = QuotaReadException(QuotaReadFailureKind.NETWORK, "offline")
}
