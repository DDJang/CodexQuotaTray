package com.codexquotatray.android.ui

import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaWindow
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.protocol.ResetCreditSnapshot
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaUiModelTest {
    @Test
    fun unauthenticatedIsAnExplicitState() {
        val model = unauthenticatedQuotaUiModel()

        assertEquals(QuotaUiStatus.UNAUTHENTICATED, model.status)
        assertTrue(model.windows.isEmpty())
    }

    @Test
    fun successfulUsageKeepsWindowDataEvenWhenPlanIsUnknown() {
        val model = direct(
            planType = null,
            windows = listOf(
                window(
                    limitId = "opaque",
                    limitName = "当前额度",
                    usedPercent = 12,
                    remainingPercent = 88,
                    duration = 300,
                    resetAt = 1_900_000_000L,
                ),
            ),
        ).toQuotaUiModel()

        assertEquals(QuotaUiStatus.LOADED, model.status)
        assertEquals("5 小时额度", model.windows.single().title)
        assertEquals(88, model.windows.single().remainingPercent)
    }

    @Test
    fun mixedCanonicalAndReserveWindowsOnlyExposeCodex() {
        val model = direct(
            windows = listOf(
                window(limitId = "primary", remainingPercent = 88, bucketId = "codex"),
                window(limitId = "reserve", remainingPercent = 40, bucketId = "gpt-reserve"),
            ),
        ).toQuotaUiModel()

        assertEquals(1, model.windows.size)
        assertEquals(88, model.windows.single().remainingPercent)
        assertNull(model.message)
    }

    @Test
    fun onlyReserveOrUnknownWindowsUseTheEmptyState() {
        for (bucketId in listOf("gpt-reserve", "future-unknown")) {
            val model = direct(
                windows = listOf(window(bucketId = bucketId)),
            ).toQuotaUiModel()

            assertTrue(model.windows.isEmpty())
            assertEquals("暂无可用额度", model.message)
        }
    }

    @Test
    fun missingValuesDoNotBecomeZero() {
        val model = direct(
            windows = listOf(window(usedPercent = null, remainingPercent = null, resetAt = null)),
        ).toQuotaUiModel()

        assertEquals(QuotaUiStatus.LOADED, model.status)
        assertNull(model.windows.single().remainingPercent)
        assertNull(model.windows.single().resetsAt)
    }

    @Test
    fun knownWindowDurationsUseHumanReadableTitles() {
        val model = direct(
            windows = listOf(
                window(duration = 300),
                window(limitId = "long", duration = 10_080),
            ),
        ).toQuotaUiModel()

        assertEquals(listOf("5 小时额度", "7 天额度"), model.windows.map { it.title })
    }

    @Test
    fun zeroWindowsRemainLoadedAndExplicit() {
        val model = direct(windows = emptyList(), quotaState = "zero_windows").toQuotaUiModel()

        assertEquals(QuotaUiStatus.LOADED, model.status)
        assertEquals("暂无可用额度", model.message)
        assertTrue(model.windows.isEmpty())
    }

    @Test
    fun unavailableQuotaIsAnErrorState() {
        val model = direct(windows = emptyList(), quotaState = "unavailable").toQuotaUiModel()

        assertEquals(QuotaUiStatus.ERROR, model.status)
        assertEquals("额度详情暂不可用", model.message)
    }

    @Test
    fun zeroResetCreditsAreHiddenFromTheQuotaPage() {
        val model = direct(
            windows = listOf(window()),
            resetCredits = ResetCreditSnapshot(availableCount = 0L, credits = emptyList()),
        ).toQuotaUiModel()

        assertNull(model.resetCredits)
    }

    @Test
    fun countOnlyResetCreditsRemainVisibleWhenDetailsAreUnavailable() {
        val model = direct(
            windows = listOf(window()),
            resetCredits = ResetCreditSnapshot(availableCount = 2L, credits = null),
        ).toQuotaUiModel()

        assertEquals(2L, model.resetCredits?.availableCount)
        assertEquals(ResetCreditDetailState.UNAVAILABLE, model.resetCredits?.detailState)
        assertTrue(model.resetCredits?.availableCredits.isNullOrEmpty())
    }

    @Test
    fun completeAndPartialDetailsOnlyExposeAvailableStatuses() {
        val complete = direct(
            windows = listOf(window()),
            resetCredits = ResetCreditSnapshot(
                availableCount = 2L,
                credits = listOf(
                    ResetCredit(id = "a", status = "available", title = "A"),
                    ResetCredit(id = "b", status = "AVAILABLE", resetType = "weekly"),
                ),
            ),
        ).toQuotaUiModel()
        val partial = direct(
            windows = listOf(window()),
            resetCredits = ResetCreditSnapshot(
                availableCount = 2L,
                credits = listOf(
                    ResetCredit(id = "a", status = "available"),
                    ResetCredit(id = "redeemed", status = "redeemed"),
                    ResetCredit(id = "unknown", status = "unknown"),
                ),
            ),
        ).toQuotaUiModel()

        assertEquals(ResetCreditDetailState.COMPLETE, complete.resetCredits?.detailState)
        assertEquals(listOf("a", "b"), complete.resetCredits?.availableCredits?.map { it.id })
        assertEquals(ResetCreditDetailState.PARTIAL, partial.resetCredits?.detailState)
        assertEquals(listOf("a"), partial.resetCredits?.availableCredits?.map { it.id })
        assertEquals(2L, partial.resetCredits?.availableCount)
    }

    @Test
    fun refreshErrorKeepsTheLastSuccessfulQuotaForStaleDisplay() {
        val previous = direct(
            windows = listOf(window(remainingPercent = 88)),
        ).toQuotaUiModel()

        val model = quotaErrorUiModel("无法连接额度服务，请检查网络", previous)

        assertEquals(QuotaUiStatus.ERROR, model.status)
        assertEquals(previous.windows, model.windows)
        assertEquals(previous.updatedAtMillis, model.updatedAtMillis)
        assertEquals("无法连接额度服务，请检查网络", model.message)
    }

    private fun direct(
        planType: String? = "plus",
        windows: List<QuotaWindow>,
        quotaState: String = if (windows.isEmpty()) "zero_windows" else "available",
        resetCredits: ResetCreditSnapshot? = null,
    ): DirectQuotaResult = DirectQuotaResult(
        planType = planType,
        windows = windows,
        quotaState = quotaState,
        updatedAtMillis = 123L,
        resetCredits = resetCredits,
    )

    private fun window(
        limitId: String = "short",
        limitName: String? = null,
        usedPercent: Int? = 10,
        remainingPercent: Int? = 90,
        duration: Long? = 300,
        resetAt: Long? = 1_900_000_000L,
        bucketId: String? = "codex",
    ): QuotaWindow = QuotaWindow(
        limitId = limitId,
        limitName = limitName,
        sourceSlot = "primary",
        usedPercent = usedPercent,
        remainingPercent = remainingPercent,
        windowDurationMins = duration,
        resetsAt = resetAt,
        bucketId = bucketId,
    )
}
