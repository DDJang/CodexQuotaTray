package com.codexquotatray.android

internal const val MIN_REFRESH_PRESENTATION_MILLIS = 700L

internal fun remainingRefreshPresentationMillis(
    startedAtMillis: Long,
    finishedAtMillis: Long,
    minimumMillis: Long = MIN_REFRESH_PRESENTATION_MILLIS,
): Long = (minimumMillis - (finishedAtMillis - startedAtMillis)).coerceAtLeast(0L)
