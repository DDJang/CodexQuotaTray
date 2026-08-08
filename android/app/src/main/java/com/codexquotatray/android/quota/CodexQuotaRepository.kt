package com.codexquotatray.android.quota

import android.content.Context
import com.codexquotatray.android.alerts.QuotaAlertEvaluator
import com.codexquotatray.android.alerts.QuotaAlertStateStore
import com.codexquotatray.android.alerts.QuotaNotificationPublisher
import com.codexquotatray.android.auth.CodexOAuthClient
import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.auth.OAuthException
import com.codexquotatray.android.auth.OAuthFailureKind
import com.codexquotatray.android.auth.OAuthLoginUpdate
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.usage.CodexUsageClient
import com.codexquotatray.android.usage.UsageException
import com.codexquotatray.android.usage.UsageFailureKind
import java.io.IOException

enum class QuotaReadFailureKind {
    LOGIN_REQUIRED,
    NETWORK,
    INVALID_RESPONSE,
    SERVER,
}

class QuotaReadException(
    val kind: QuotaReadFailureKind,
    override val message: String,
    cause: Throwable? = null,
) : IOException(message, cause)

/**
 * The product data path is deliberately small: OAuth credentials -> usage HTTPS API -> UI.
 * App Server remains only in the historical P0 diagnostic tools.
 */
class CodexQuotaRepository(
    context: Context,
    private val credentialStore: OAuthStore = OAuthStore(context),
    private val oauthClient: CodexOAuthClient = CodexOAuthClient(),
    private val usageClient: CodexUsageClient = CodexUsageClient(),
    private val alertStateStore: QuotaAlertStateStore = QuotaAlertStateStore(context),
    private val notificationPublisher: QuotaNotificationPublisher =
        QuotaNotificationPublisher(context),
    private val snapshotStore: QuotaSnapshotStore = QuotaSnapshotStore(context),
) {
    fun refresh(): DirectQuotaResult {
        var credentials = credentialStore.load()
            ?: throw QuotaReadException(QuotaReadFailureKind.LOGIN_REQUIRED, "尚未登录 Codex")

        if (credentials.needsRefresh()) {
            credentials = refreshCredentials(credentials, allowStaleAccess = true)
        }
        return fetchWithRecovery(credentials)
    }

    fun login(onUpdate: (OAuthLoginUpdate) -> Unit = {}): DirectQuotaResult {
        val credentials = try {
            oauthClient.login(onUpdate)
        } catch (error: OAuthException) {
            throw mapOAuthFailure(error)
        }
        saveCredentials(credentials)
        return fetchWithRecovery(credentials)
    }

    private fun fetchWithRecovery(initial: OAuthCredentials): DirectQuotaResult {
        try {
            return publishAndReturn(usageClient.fetch(initial))
        } catch (error: UsageException) {
            if (error.kind != UsageFailureKind.UNAUTHORIZED) throw mapUsageFailure(error)
            if (initial.refreshToken.isBlank()) {
                credentialStore.clear()
                throw QuotaReadException(QuotaReadFailureKind.LOGIN_REQUIRED, "登录已失效，请重新登录")
            }
        }

        val refreshed = refreshCredentials(initial, allowStaleAccess = false)
        return try {
            publishAndReturn(usageClient.fetch(refreshed))
        } catch (error: UsageException) {
            if (error.kind == UsageFailureKind.UNAUTHORIZED) {
                credentialStore.clear()
                throw QuotaReadException(QuotaReadFailureKind.LOGIN_REQUIRED, "登录已失效，请重新登录")
            }
            throw mapUsageFailure(error)
        }
    }

    private fun refreshCredentials(
        credentials: OAuthCredentials,
        allowStaleAccess: Boolean,
    ): OAuthCredentials {
        return try {
            oauthClient.refresh(credentials).also(::saveCredentials)
        } catch (error: OAuthException) {
            when {
                isPermanentAuthFailure(error.kind) -> {
                    credentialStore.clear()
                    throw QuotaReadException(
                        QuotaReadFailureKind.LOGIN_REQUIRED,
                        "登录已失效，请重新登录",
                        error,
                    )
                }

                allowStaleAccess && error.kind == OAuthFailureKind.NETWORK -> credentials
                allowStaleAccess && error.kind == OAuthFailureKind.SERVER -> credentials
                else -> throw mapOAuthFailure(error)
            }
        }
    }

    private fun publishAndReturn(result: DirectQuotaResult): DirectQuotaResult {
        if (result.quotaState != "unavailable") {
            snapshotStore.save(result)
            val events = QuotaAlertEvaluator(alertStateStore).evaluate(result.windows)
            alertStateStore.markSuccessfulRefresh(result.updatedAtMillis)
            notificationPublisher.publish(events)
        }
        return result
    }

    private fun saveCredentials(credentials: OAuthCredentials) {
        if (!credentialStore.save(credentials)) {
            throw QuotaReadException(
                QuotaReadFailureKind.SERVER,
                "认证信息无法保存到本机",
            )
        }
    }

    private fun mapUsageFailure(error: UsageException): QuotaReadException = when (error.kind) {
        UsageFailureKind.UNAUTHORIZED -> QuotaReadException(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            "登录已失效，请重新登录",
            error,
        )

        UsageFailureKind.NETWORK -> QuotaReadException(
            QuotaReadFailureKind.NETWORK,
            "无法连接额度服务，请检查网络",
            error,
        )

        UsageFailureKind.INVALID_RESPONSE -> QuotaReadException(
            QuotaReadFailureKind.INVALID_RESPONSE,
            "额度服务返回了无法识别的数据",
            error,
        )

        UsageFailureKind.SERVER -> QuotaReadException(
            QuotaReadFailureKind.SERVER,
            "额度服务暂时不可用",
            error,
        )
    }

    private fun mapOAuthFailure(error: OAuthException): QuotaReadException = when (error.kind) {
        OAuthFailureKind.DEVICE_AUTH_DISABLED -> QuotaReadException(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            error.message,
            error,
        )

        OAuthFailureKind.LOGIN_REQUIRED,
        OAuthFailureKind.REFRESH_EXPIRED,
        OAuthFailureKind.REFRESH_REVOKED,
        OAuthFailureKind.REFRESH_REUSED,
        -> QuotaReadException(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            "登录已失效，请重新登录",
            error,
        )

        OAuthFailureKind.NETWORK -> QuotaReadException(
            QuotaReadFailureKind.NETWORK,
            "无法连接认证服务，请检查网络",
            error,
        )

        OAuthFailureKind.INVALID_RESPONSE -> QuotaReadException(
            QuotaReadFailureKind.INVALID_RESPONSE,
            "认证服务返回了无法识别的数据",
            error,
        )

        OAuthFailureKind.SERVER -> QuotaReadException(
            QuotaReadFailureKind.SERVER,
            "认证服务暂时不可用",
            error,
        )
    }

    private fun isPermanentAuthFailure(kind: OAuthFailureKind): Boolean = kind in setOf(
        OAuthFailureKind.LOGIN_REQUIRED,
        OAuthFailureKind.REFRESH_EXPIRED,
        OAuthFailureKind.REFRESH_REVOKED,
        OAuthFailureKind.REFRESH_REUSED,
    )
}
