package com.codexquotatray.android.usage

import com.codexquotatray.android.source.DataSourcePriority
import com.codexquotatray.android.source.DataSourcePrioritySettings
import com.codexquotatray.android.source.DataSourcePriorityStore
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.time.LocalDate

class TokenUsageSourceRouterTest {
    @Test fun tokenRouterSupportsBothPriorityDirectionsAndFallback() {
        listOf(
            Triple(DataSourcePriority.OPENAI_FIRST, false, DataTransport.OPENAI),
            Triple(DataSourcePriority.OPENAI_FIRST, true, DataTransport.WINDOWS),
            Triple(DataSourcePriority.WINDOWS_FIRST, false, DataTransport.WINDOWS),
            Triple(DataSourcePriority.WINDOWS_FIRST, true, DataTransport.OPENAI),
        ).forEach { (priority, failPreferred, expected) ->
            val router = router(priority, failPreferred)
            assertEquals(expected, router.read(false).snapshot.transport)
        }
    }

    @Test fun missingProvidersAreSkippedAndBothUnavailableFails() {
        val windowsOnly = router(DataSourcePriority.OPENAI_FIRST, false, hasOpenAI = false)
        assertEquals(DataTransport.WINDOWS, windowsOnly.read(false).snapshot.transport)
        val none = router(DataSourcePriority.OPENAI_FIRST, false, hasOpenAI = false, hasWindows = false)
        assertThrows(TokenUsageException::class.java) { none.read(false) }
    }

    private fun router(
        priority: DataSourcePriority,
        failPreferred: Boolean,
        hasOpenAI: Boolean = true,
        hasWindows: Boolean = true,
    ): TokenUsageSourceRouter {
        val openAI = TokenUsageProvider {
            if (failPreferred && priority == DataSourcePriority.OPENAI_FIRST) throw failure()
            TokenUsageSourceRead(snapshot(DataTransport.OPENAI, TokenUsageScope.ACCOUNT))
        }
        val windows = TokenUsageProvider {
            if (failPreferred && priority == DataSourcePriority.WINDOWS_FIRST) throw failure()
            TokenUsageSourceRead(snapshot(DataTransport.WINDOWS, TokenUsageScope.LOCAL))
        }
        return TokenUsageSourceRouter(
            priorityStore = object : DataSourcePriorityStore {
                override fun load() = DataSourcePrioritySettings(token = priority)
                override fun save(value: DataSourcePrioritySettings) = true
            },
            hasOpenAI = { hasOpenAI },
            hasWindows = { hasWindows },
            openAI = openAI,
            windows = windows,
        )
    }

    private fun failure() = TokenUsageException(TokenUsageFailureKind.OFFLINE, "offline")
}

internal fun snapshot(
    transport: DataTransport = DataTransport.WINDOWS,
    scope: TokenUsageScope = TokenUsageScope.LOCAL,
) = TokenUsageSnapshot(
    1,
    "2026-08-23T00:00:00Z",
    "UTC",
    TokenUsageSummary(1, 1, 1, 1, 1, LocalDate.parse("2026-08-23"), 1, 1, 1),
    listOf(TokenUsageDay(LocalDate.parse("2026-08-23"), 1, null, null, null, null)),
    transport,
    scope,
)
