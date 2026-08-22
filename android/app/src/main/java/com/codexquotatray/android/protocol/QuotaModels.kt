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
    val bucketId: String? = null,
)

data class DirectQuotaResult(
    val planType: String?,
    val windows: List<QuotaWindow>,
    val quotaState: String,
    val updatedAtMillis: Long,
    val source: QuotaSource = QuotaSource.DIRECT,
    val resetCredits: ResetCreditSnapshot? = null,
)

/** The usage response's authoritative count plus the optional detail response. */
data class ResetCreditSnapshot(
    val availableCount: Long?,
    /** Null means the detail request was unavailable; an empty list is a successful empty response. */
    val credits: List<ResetCredit>? = null,
)

/** Raw reset-credit fields retained across Direct, LAN, and the local quota snapshot. */
data class ResetCredit(
    val id: String? = null,
    val resetType: String? = null,
    val status: String? = null,
    val grantedAt: Long? = null,
    val expiresAt: Long? = null,
    val title: String? = null,
    val description: String? = null,
)

enum class QuotaSource {
    DIRECT,
    WINDOWS,
}
