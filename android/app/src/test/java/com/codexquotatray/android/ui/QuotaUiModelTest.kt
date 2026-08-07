package com.codexquotatray.android.ui

import com.codexquotatray.android.protocol.InitializeDiagnostic
import com.codexquotatray.android.protocol.ProtocolProbeResult
import com.codexquotatray.android.protocol.QuotaWindow
import com.codexquotatray.android.protocol.ReadyProbeResult
import com.codexquotatray.android.protocol.WebSocketDiagnostic
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaUiModelTest {
    @Test
    fun unauthenticatedDoesNotBecomeLoaded() {
        val model = probe(
            authenticated = false,
            rateLimitsResult = "unauthenticated",
            rateLimitsReadSucceeded = false,
        ).toQuotaUiModel(nowMillis = 123L)

        assertEquals(QuotaUiStatus.UNAUTHENTICATED, model.status)
        assertTrue(model.windows.isEmpty())
    }

    @Test
    fun successfulRateLimitsTakePrecedenceOverStaleAuthenticationFlag() {
        val model = probe(
            authenticated = false,
            windows = listOf(
                QuotaWindow(
                    limitId = "opaque",
                    limitName = "当前额度",
                    sourceSlot = "custom",
                    usedPercent = 12,
                    remainingPercent = 88,
                    windowDurationMins = 300,
                    resetsAt = 1234L,
                ),
            ),
        ).toQuotaUiModel(nowMillis = 123L)

        assertEquals(QuotaUiStatus.LOADED, model.status)
        assertEquals(88, model.windows.single().remainingPercent)
    }

    @Test
    fun dynamicWindowsPreserveMissingResetTime() {
        val model = probe(
            authenticated = true,
            windows = listOf(
                QuotaWindow(
                    limitId = "opaque",
                    limitName = "Codex",
                    planType = "plus",
                    sourceSlot = "custom",
                    usedPercent = 28,
                    remainingPercent = 72,
                    windowDurationMins = 300,
                    resetsAt = null,
                ),
            ),
        ).toQuotaUiModel(nowMillis = 123L)

        assertEquals(QuotaUiStatus.LOADED, model.status)
        assertEquals("Plus", model.accountLabel)
        assertEquals(72, model.windows.single().remainingPercent)
        assertNull(model.windows.single().resetsAt)
        assertEquals(123L, model.updatedAtMillis)
    }

    @Test
    fun rateLimitsRpcErrorIsAnErrorState() {
        val model = probe(
            authenticated = true,
            rateLimitsResult = "rpc_error",
            rateLimitsReadSucceeded = false,
        ).toQuotaUiModel()

        assertEquals(QuotaUiStatus.ERROR, model.status)
        assertEquals("额度读取失败", model.message)
    }

    @Test
    fun connectionFailureUsesShortUserFacingMessage() {
        val model = probe(
            authenticated = null,
            rateLimitsResult = "not_attempted",
            rateLimitsReadSucceeded = false,
            lastError = "connection_refused",
        ).toQuotaUiModel()

        assertEquals(QuotaUiStatus.ERROR, model.status)
        assertEquals("无法连接 Codex", model.message)
    }

    private fun probe(
        authenticated: Boolean?,
        windows: List<QuotaWindow> = emptyList(),
        quotaState: String = if (windows.isEmpty()) "zero_windows" else "available",
        rateLimitsResult: String = "succeeded",
        rateLimitsReadSucceeded: Boolean = true,
        lastError: String? = null,
    ): ProtocolProbeResult = ProtocolProbeResult(
        readyProbe = ReadyProbeResult(true, true, "ready", 200, "OK", 1),
        webSocket = WebSocketDiagnostic.default(43128).copy(onOpen = true),
        initialize = InitializeDiagnostic(true, true, "success"),
        initializeSucceeded = true,
        accountResult = if (authenticated == false) "unauthenticated" else "succeeded",
        rateLimitsResult = rateLimitsResult,
        rateLimitsReadSucceeded = rateLimitsReadSucceeded,
        quotaWindowCount = windows.size,
        quotaWindows = windows,
        quotaState = quotaState,
        authenticated = authenticated,
        malformedJsonCount = 0,
        flowCompleted = true,
        lastError = lastError,
    )
}
