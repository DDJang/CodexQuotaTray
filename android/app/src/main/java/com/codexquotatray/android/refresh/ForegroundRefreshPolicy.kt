package com.codexquotatray.android.refresh

/**
 * Keeps page-entry reads inexpensive while allowing an explicit user action to
 * always refresh. Timestamps are local successful-read times in milliseconds.
 */
internal object ForegroundRefreshPolicy {
    const val FRESHNESS_WINDOW_MILLIS = 2 * 60 * 1_000L

    fun shouldRunOnVisible(
        enabled: Boolean,
        lastSuccessfulAtMillis: Long?,
        nowMillis: Long = System.currentTimeMillis(),
    ): Boolean = enabled && isStale(lastSuccessfulAtMillis, nowMillis)

    fun shouldRunManually(): Boolean = true

    private fun isStale(lastSuccessfulAtMillis: Long?, nowMillis: Long): Boolean =
        lastSuccessfulAtMillis == null || nowMillis - lastSuccessfulAtMillis >= FRESHNESS_WINDOW_MILLIS
}
