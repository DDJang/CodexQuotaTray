package com.codexquotatray.android.protocol

/** Shared quota window shape used by the direct API and retained P0 diagnostics. */
data class QuotaWindow(
    val limitId: String?,
    val limitName: String?,
    val planType: String? = null,
    val sourceSlot: String,
    val usedPercent: Int?,
    val remainingPercent: Int?,
    val windowDurationMins: Long?,
    val resetsAt: Long?,
)

data class DirectQuotaResult(
    val planType: String?,
    val windows: List<QuotaWindow>,
    val quotaState: String,
    val updatedAtMillis: Long,
    val source: QuotaSource = QuotaSource.DIRECT,
)

enum class QuotaSource {
    DIRECT,
    WINDOWS,
}
