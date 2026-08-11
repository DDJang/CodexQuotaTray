package com.codexquotatray.android.refresh

/**
 * Policy for automatic refreshes initiated by the application lifecycle.
 *
 * The timestamp is the last automatic *attempt*, not the last successful
 * response.  That distinction prevents a failed request from becoming a fast
 * retry loop when the app returns to the foreground.
 */
internal object ForegroundRefreshPolicy {
    const val FRESHNESS_WINDOW_MILLIS = 2 * 60 * 1_000L

    fun shouldRunOnForeground(
        enabled: Boolean,
        lastAttemptAtMillis: Long?,
        nowMillis: Long = System.currentTimeMillis(),
    ): Boolean = enabled && isStale(lastAttemptAtMillis, nowMillis)

    fun shouldRunManually(): Boolean = true

    private fun isStale(lastAttemptAtMillis: Long?, nowMillis: Long): Boolean =
        lastAttemptAtMillis == null || nowMillis - lastAttemptAtMillis >= FRESHNESS_WINDOW_MILLIS
}
