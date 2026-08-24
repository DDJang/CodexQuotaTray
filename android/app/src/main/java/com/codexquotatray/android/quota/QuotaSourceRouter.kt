package com.codexquotatray.android.quota

import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.source.DataSourcePriority
import com.codexquotatray.android.usage.TokenSyncPairing

internal data class QuotaSourceRead(
    val quota: DirectQuotaResult,
    val pairing: TokenSyncPairing? = null,
)

class QuotaSourceRouter {
    internal fun read(
        priority: DataSourcePriority,
        hasOpenAI: Boolean,
        hasWindows: Boolean,
        openAI: () -> QuotaSourceRead,
        windows: () -> QuotaSourceRead,
    ): QuotaSourceRead {
        val providers = when (priority) {
            DataSourcePriority.OPENAI_FIRST -> listOf(hasOpenAI to openAI, hasWindows to windows)
            DataSourcePriority.WINDOWS_FIRST -> listOf(hasWindows to windows, hasOpenAI to openAI)
        }
        var firstUnavailable: QuotaSourceRead? = null
        var firstFailure: Exception? = null
        var firstRetryableFailure: Exception? = null
        providers.forEach { (available, provider) ->
            if (!available) return@forEach
            try {
                val result = provider()
                if (result.quota.quotaState != "unavailable") return result
                firstUnavailable = firstUnavailable ?: result
            } catch (failure: Exception) {
                firstFailure = firstFailure ?: failure
                if (failure is QuotaReadException && failure.kind in RETRYABLE_QUOTA_FAILURES) {
                    firstRetryableFailure = firstRetryableFailure ?: failure
                }
            }
        }
        firstRetryableFailure?.let { throw it }
        firstUnavailable?.let { return it }
        throw firstFailure ?: QuotaReadException(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            "尚未配置可用的额度数据来源",
        )
    }
}

private val RETRYABLE_QUOTA_FAILURES = setOf(
    QuotaReadFailureKind.NETWORK,
    QuotaReadFailureKind.SERVER,
)
