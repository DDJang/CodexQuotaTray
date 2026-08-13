package com.codexquotatray.android.refresh

internal enum class BackgroundRetryDecision {
    RETRY,
    EXHAUSTED,
    PERMANENT,
}

/** Shared bounded WorkManager retry policy for the independent quota and Token channels. */
internal object BackgroundRefreshRetryPolicy {
    const val MAX_RETRY_ATTEMPTS = 2
    const val BACKOFF_MINUTES = 5L

    fun transientDecision(runAttemptCount: Int): BackgroundRetryDecision =
        if (runAttemptCount < MAX_RETRY_ATTEMPTS) {
            BackgroundRetryDecision.RETRY
        } else {
            BackgroundRetryDecision.EXHAUSTED
        }

    fun reason(runAttemptCount: Int): AutomaticRefreshReason =
        if (runAttemptCount > 0) AutomaticRefreshReason.RETRY else AutomaticRefreshReason.SCHEDULED
}
