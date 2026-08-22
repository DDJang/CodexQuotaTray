package com.codexquotatray.android.protocol

/**
 * Defines which server bucket is safe to expose in user-facing quota views.
 * Raw quota windows remain available to diagnostics and persistence.
 */
internal object QuotaBucketPolicy {
    const val CANONICAL_BUCKET_ID = "codex"

    fun isCanonical(bucketId: String?): Boolean =
        bucketId != null && bucketId.trim().equals(CANONICAL_BUCKET_ID, ignoreCase = true)

    fun isCanonical(window: QuotaWindow, source: QuotaSource): Boolean {
        if (window.bucketId != null) return isCanonical(window.bucketId)

        // Direct snapshots written before bucketId was persisted have a known
        // legacy shape. Do not apply this compatibility rule to Windows data,
        // whose missing bucket metadata is ambiguous.
        return source == QuotaSource.DIRECT
            && window.sourceSlot == window.limitId
            && window.limitId in setOf("primary", "secondary")
    }

    fun canonicalWindows(result: DirectQuotaResult): List<QuotaWindow> =
        result.windows.filter { isCanonical(it, result.source) }
}
