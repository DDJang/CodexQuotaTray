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

    @Test fun unavailableOpenAIFirstFallsBackToWindows() {
        val result = router.read(
            DataSourcePriority.OPENAI_FIRST,
            hasOpenAI = true,
            hasWindows = true,
            openAI = { unavailable("openai") },
            windows = { read("windows") },
        )
        assertEquals("windows", result.quota.planType)
    }

    @Test fun unavailableWindowsFirstFallsBackToOpenAI() {
        val result = router.read(
            DataSourcePriority.WINDOWS_FIRST,
            hasOpenAI = true,
            hasWindows = true,
            openAI = { read("openai") },
            windows = { unavailable("windows") },
        )
        assertEquals("openai", result.quota.planType)
    }

    @Test fun bothProvidersUnavailableReturnsFirstUnavailableResult() {
        val result = router.read(
            DataSourcePriority.OPENAI_FIRST,
            hasOpenAI = true,
            hasWindows = true,
            openAI = { unavailable("openai") },
            windows = { unavailable("windows") },
        )
        assertEquals("openai", result.quota.planType)
        assertEquals("unavailable", result.quota.quotaState)
    }

    @Test fun retryableFailureWinsOverUnavailableResult() {
        val error = assertThrows(QuotaReadException::class.java) {
            router.read(
                DataSourcePriority.OPENAI_FIRST,
                hasOpenAI = true,
                hasWindows = true,
                openAI = { unavailable("openai") },
                windows = { throw failure(QuotaReadFailureKind.NETWORK) },
            )
        }
        assertEquals(QuotaReadFailureKind.NETWORK, error.kind)
    }

    @Test fun retryableQuotaFailureWinsOverPermanentFailureInEitherOrder() {
        assertFailure(
            DataSourcePriority.OPENAI_FIRST,
            first = QuotaReadFailureKind.LOGIN_REQUIRED,
            second = QuotaReadFailureKind.NETWORK,
        )
        assertFailure(
            DataSourcePriority.WINDOWS_FIRST,
            first = QuotaReadFailureKind.LOGIN_REQUIRED,
            second = QuotaReadFailureKind.NETWORK,
        )
        assertFailure(
            DataSourcePriority.OPENAI_FIRST,
            first = QuotaReadFailureKind.NETWORK,
            second = QuotaReadFailureKind.LOGIN_REQUIRED,
        )
        assertFailure(
            DataSourcePriority.WINDOWS_FIRST,
            first = QuotaReadFailureKind.NETWORK,
            second = QuotaReadFailureKind.LOGIN_REQUIRED,
        )
    }

    @Test fun quotaRetryableServerFailureIsPreferred() {
        assertFailure(
            DataSourcePriority.OPENAI_FIRST,
            first = QuotaReadFailureKind.INVALID_RESPONSE,
            second = QuotaReadFailureKind.SERVER,
            expected = QuotaReadFailureKind.SERVER,
        )
    }

    @Test fun quotaPermanentFailuresKeepProviderOrder() {
        assertFailure(
            DataSourcePriority.OPENAI_FIRST,
            first = QuotaReadFailureKind.LOGIN_REQUIRED,
            second = QuotaReadFailureKind.INVALID_RESPONSE,
            expected = QuotaReadFailureKind.LOGIN_REQUIRED,
        )
    }

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
            openAI = {
                if (failPreferred && preferred == "openai") {
                    throw failure(QuotaReadFailureKind.NETWORK)
                } else {
                    read("openai")
                }
            },
            windows = {
                if (failPreferred && preferred == "windows") {
                    throw failure(QuotaReadFailureKind.NETWORK)
                } else {
                    read("windows")
                }
            },
        )
        assertEquals(expected, result.quota.planType)
    }

    private fun assertFailure(
        priority: DataSourcePriority,
        first: QuotaReadFailureKind,
        second: QuotaReadFailureKind,
        expected: QuotaReadFailureKind = QuotaReadFailureKind.NETWORK,
    ) {
        val error = assertThrows(QuotaReadException::class.java) {
            router.read(
                priority,
                hasOpenAI = true,
                hasWindows = true,
                openAI = {
                    throw failure(if (priority == DataSourcePriority.OPENAI_FIRST) first else second)
                },
                windows = {
                    throw failure(if (priority == DataSourcePriority.OPENAI_FIRST) second else first)
                },
            )
        }
        assertEquals(expected, error.kind)
    }

    private fun read(name: String, quotaState: String = "available") =
        QuotaSourceRead(DirectQuotaResult(name, emptyList(), quotaState, 1L))

    private fun unavailable(name: String) = read(name, quotaState = "unavailable")

    private fun failure(kind: QuotaReadFailureKind) = QuotaReadException(kind, kind.name)
}
