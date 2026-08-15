package com.codexquotatray.android.quota

import android.content.Context
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.alerts.QuotaAlertEvaluator
import com.codexquotatray.android.alerts.QuotaAlertStateStore
import com.codexquotatray.android.alerts.QuotaNotificationPublisher
import com.codexquotatray.android.auth.CodexOAuthClient
import com.codexquotatray.android.auth.CodexProcessLock
import com.codexquotatray.android.auth.CredentialGeneration
import com.codexquotatray.android.auth.CredentialRefreshAbortedException
import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.auth.OAuthException
import com.codexquotatray.android.auth.OAuthFailureKind
import com.codexquotatray.android.auth.OAuthLoginUpdate
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.auth.ProcessCredentialRefreshCoordinator
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.usage.CodexUsageClient
import com.codexquotatray.android.usage.AndroidLanDiagnosticLogger
import com.codexquotatray.android.usage.QuotaNetworkTimeouts
import com.codexquotatray.android.usage.TokenSyncPairingStore
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenUsagePairingLifecycle
import com.codexquotatray.android.usage.UsageException
import com.codexquotatray.android.usage.UsageFailureKind
import com.codexquotatray.android.usage.WindowsQuotaFallback
import com.codexquotatray.android.usage.WindowsQuotaFallbackClient
import com.codexquotatray.android.usage.cacheIdentity
import com.codexquotatray.android.usage.matchesConfiguration
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

internal fun commitIfPairingCurrent(
    pairingStore: TokenSyncPairingStore,
    expectedPairing: TokenSyncPairing,
    commit: () -> Boolean,
): Boolean = TokenUsagePairingLifecycle.withLock {
    if (pairingStore.load()?.matchesConfiguration(expectedPairing) != true) false else commit()
}

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
    private val tokenSyncStore: TokenSyncPairingStore = TokenSyncStore(context),
    private val lanAvailability: LanAvailability = AndroidLanAvailability(context),
    private val windowsQuotaFallback: WindowsQuotaFallback = WindowsQuotaFallbackClient(context),
) {
    private val appContext = context.applicationContext
    private val alertEvaluator by lazy { QuotaAlertEvaluator(alertStateStore) }
    private val fallbackResolver by lazy {
        WindowsQuotaFallbackResolver(
            pairingStore = tokenSyncStore,
            lanAvailability = lanAvailability,
            fallbackClient = windowsQuotaFallback,
            recordFailure = { failure ->
                AppLogStore.record(appContext, "Windows 局域网额度 fallback 未成功：${failure.kind}", "WARN")
            },
            diagnostics = AndroidLanDiagnosticLogger(appContext),
        )
    }
    private val successCommitter by lazy {
        QuotaSuccessfulRefreshCommitter(
            saveSnapshot = { result, completedAtMillis, windowsDeviceIdentity ->
                snapshotStore.save(result, completedAtMillis, windowsDeviceIdentity)
            },
            evaluateAlerts = alertEvaluator::evaluate,
            markSuccessfulRefresh = alertStateStore::markSuccessfulRefresh,
            publishNotifications = notificationPublisher::publish,
            restoreAlerts = alertEvaluator::restoreLastEvaluation,
            publishWidget = { result, _, _ ->
                runCatching {
                    com.codexquotatray.android.widget.QuotaWidgetBridge.publish(
                        appContext,
                        result,
                    )
                }.onFailure {
                    AppLogStore.record(appContext, "额度小组件更新失败", "DEBUG")
                }
            },
        )
    }

    fun refresh(): DirectQuotaResult {
        val generation = CredentialGeneration.current()
        val loaded = credentialStore.load()
        if (loaded == null) {
            val resolved = fallbackResolver.fetchWindowsOnlyWithPairing()
            return commitAndReturn(resolved.quota, resolved.pairing)
        }
        var credentials = currentCredentialsOrThrow(generation)

        if (credentials.needsRefresh()) {
            credentials = refreshCredentials(
                observed = credentials,
                allowStaleAccess = true,
                observedGeneration = generation,
            )
        }
        val fetched = fetchWithRecovery(credentials, generation)
        return publishAndReturn(fetched.quota, fetched.credentials, generation, fetched.pairing)
    }

    fun login(onUpdate: (OAuthLoginUpdate) -> Unit = {}): OAuthCredentials {
        val generation = CredentialGeneration.current()
        val credentials = try {
            oauthClient.login(onUpdate)
        } catch (error: OAuthException) {
            throw mapOAuthFailure(error)
        }

        synchronized(CodexProcessLock.monitor) {
            if (CredentialGeneration.current() != generation) {
                throw QuotaReadException(
                    QuotaReadFailureKind.LOGIN_REQUIRED,
                    "登录状态已改变，请重新登录",
                )
            }
            saveCredentials(credentials)
        }
        return credentials
    }

    private fun fetchWithRecovery(
        initial: OAuthCredentials,
        initialGeneration: Long,
    ): SuccessfulQuotaFetch {
        var successfulCredentials = initial
        val resolved = fallbackResolver.fetchWithPairing {
            fetchDirectWithRecovery(
                initial,
                initialGeneration,
                QuotaNetworkTimeouts.directCallTimeoutMillis(
                    windowsPairingOnWifi = tokenSyncStore.load() != null && lanAvailability.isAvailable(),
                ),
                onCredentialsUsed = { successfulCredentials = it },
            )
        }
        return SuccessfulQuotaFetch(resolved.quota, successfulCredentials, resolved.pairing)
    }

    private fun fetchDirectWithRecovery(
        initial: OAuthCredentials,
        initialGeneration: Long,
        directCallTimeoutMillis: Long,
        onCredentialsUsed: (OAuthCredentials) -> Unit,
    ): DirectQuotaResult {
        try {
            onCredentialsUsed(initial)
            return usageClient.fetch(initial, directCallTimeoutMillis)
        } catch (error: UsageException) {
            if (error.kind != UsageFailureKind.UNAUTHORIZED) throw mapUsageFailure(error)
        }

        val refreshed = if (initial.refreshToken.isBlank()) {
            currentCredentialsAfterUnauthorized(initial, initialGeneration)
        } else {
            refreshCredentials(
                observed = initial,
                allowStaleAccess = false,
                observedGeneration = initialGeneration,
            )
        }
        val retryGeneration = initialGeneration
        return try {
            onCredentialsUsed(refreshed)
            usageClient.fetch(refreshed, directCallTimeoutMillis)
        } catch (error: UsageException) {
            if (error.kind == UsageFailureKind.UNAUTHORIZED) {
                clearIfCurrent(refreshed, retryGeneration)
                throw QuotaReadException(QuotaReadFailureKind.LOGIN_REQUIRED, "登录已失效，请重新登录")
            }
            throw mapUsageFailure(error)
        }
    }

    private fun refreshCredentials(
        observed: OAuthCredentials,
        allowStaleAccess: Boolean,
        observedGeneration: Long,
    ): OAuthCredentials = try {
        ProcessCredentialRefreshCoordinator.instance.refresh(
            observed = observed,
            observedGeneration = observedGeneration,
            currentGeneration = CredentialGeneration::current,
            loadCurrent = credentialStore::load,
            performRefresh = { credentials -> oauthClient.refresh(credentials) },
            saveRefreshed = ::saveCredentials,
            onFailure = { _, error ->
                if (error is OAuthException) {
                    AppLogStore.record(
                        appContext,
                        "OAuth token refresh 失败：${error.message ?: "未知错误"}",
                        "WARN",
                    )
                    if (isPermanentAuthFailure(error.kind)) {
                        credentialStore.clear()
                    }
                }
            },
        )
    } catch (error: CredentialRefreshAbortedException) {
        throw QuotaReadException(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            "登录状态已改变，请重新登录",
            error,
        )
    } catch (error: OAuthException) {
        when {
            isPermanentAuthFailure(error.kind) -> throw QuotaReadException(
                QuotaReadFailureKind.LOGIN_REQUIRED,
                "登录已失效，请重新登录",
                error,
            )

            allowStaleAccess && error.kind in setOf(
                OAuthFailureKind.NETWORK,
                OAuthFailureKind.SERVER,
            ) -> credentialStore.load()
                ?: throw QuotaReadException(
                    QuotaReadFailureKind.LOGIN_REQUIRED,
                    "登录状态已改变，请重新登录",
                    error,
                )

            else -> throw mapOAuthFailure(error)
        }
    }

    private fun currentCredentialsOrThrow(
        generation: Long,
    ): OAuthCredentials = synchronized(CodexProcessLock.monitor) {
        val current = credentialStore.load()
        if (current == null || CredentialGeneration.current() != generation) {
            throw QuotaReadException(QuotaReadFailureKind.LOGIN_REQUIRED, "尚未登录 Codex")
        }
        current
    }

    private fun currentCredentialsAfterUnauthorized(
        observed: OAuthCredentials,
        generation: Long,
    ): OAuthCredentials {
        var replacement: OAuthCredentials? = null
        synchronized(CodexProcessLock.monitor) {
            val current = credentialStore.load()
            if (CredentialGeneration.current() != generation) {
                // The request started before an explicit logout; it must not
                // continue with credentials from a later login session.
            } else if (current != null && current != observed) {
                replacement = current
            } else if (current == observed) {
                credentialStore.clear()
            }
        }
        return replacement ?: throw QuotaReadException(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            "登录已失效，请重新登录",
        )
    }

    private fun clearIfCurrent(expected: OAuthCredentials, generation: Long) {
        synchronized(CodexProcessLock.monitor) {
            val current = credentialStore.load()
            if (current == expected && CredentialGeneration.current() == generation) {
                credentialStore.clear()
            }
        }
    }

    private fun publishAndReturn(
        result: DirectQuotaResult,
        credentials: OAuthCredentials,
        generation: Long,
        expectedPairing: TokenSyncPairing?,
    ): DirectQuotaResult {
        synchronized(CodexProcessLock.monitor) {
            val current = credentialStore.load()
            if (current != credentials || CredentialGeneration.current() != generation) {
                return@synchronized
            }
            if (result.quotaState != "unavailable") {
                commitAndReturn(result, expectedPairing)
            }
        }
        return result
    }

    private fun commitAndReturn(
        result: DirectQuotaResult,
        expectedPairing: TokenSyncPairing?,
    ): DirectQuotaResult {
        if (result.quotaState != "unavailable") {
            if (expectedPairing == null) {
                successCommitter.commit(result)
            } else {
                val committed = commitIfPairingCurrent(tokenSyncStore, expectedPairing) {
                    successCommitter.commit(result, expectedPairing.cacheIdentity())
                }
                if (!committed) {
                    throw QuotaReadException(QuotaReadFailureKind.NETWORK, "Windows 配对已变更，请重试")
                }
            }
        }
        return result
    }

    private data class SuccessfulQuotaFetch(
        val quota: DirectQuotaResult,
        val credentials: OAuthCredentials,
        val pairing: TokenSyncPairing?,
    )

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
            OAuthException.NETWORK_ERROR_MESSAGE,
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
