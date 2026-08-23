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
        var firstFailure: Throwable? = null
        providers.forEach { (available, provider) ->
            if (!available) return@forEach
            try {
                return provider()
            } catch (failure: Exception) {
                firstFailure = firstFailure ?: failure
            }
        }
        throw firstFailure ?: QuotaReadException(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            "尚未配置可用的额度数据来源",
        )
    }
}
