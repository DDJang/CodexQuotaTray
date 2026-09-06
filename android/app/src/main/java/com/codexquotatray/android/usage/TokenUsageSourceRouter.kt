package com.codexquotatray.android.usage

import android.content.Context
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.auth.OAuthRefreshReason
import com.codexquotatray.android.auth.CodexOAuthClient
import com.codexquotatray.android.auth.CodexProcessLock
import com.codexquotatray.android.auth.CredentialGeneration
import com.codexquotatray.android.auth.CredentialRefreshAbortedException
import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.auth.OAuthException
import com.codexquotatray.android.auth.OAuthFailureKind
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.auth.ProcessCredentialRefreshCoordinator
import com.codexquotatray.android.source.AndroidDataSourcePriorityStore
import com.codexquotatray.android.source.DataSourcePriority
import com.codexquotatray.android.source.DataSourcePriorityStore

internal data class TokenUsageSourceRead(
    val snapshot: TokenUsageSnapshot,
    val pairing: TokenSyncPairing? = null,
    val expectedPairing: TokenSyncPairing? = pairing,
    val identityStillCurrent: () -> Boolean = { true },
)

internal fun interface TokenUsageProvider {
    fun read(forceRefresh: Boolean): TokenUsageSourceRead
}

internal class TokenUsageSourceRouter(
    private val priorityStore: DataSourcePriorityStore,
    private val hasOpenAI: () -> Boolean,
    private val hasWindows: () -> Boolean,
    private val openAI: TokenUsageProvider,
    private val windows: TokenUsageProvider,
    private val identity: () -> String = { "token-router" },
) {
    constructor(context: Context) : this(
        priorityStore = AndroidDataSourcePriorityStore(context),
        hasOpenAI = { OAuthStore(context).load() != null },
        hasWindows = { TokenSyncStore(context).load() != null },
        openAI = OpenAIAccountTokenUsageProvider(context),
        windows = WindowsTokenUsageProvider(context),
        identity = {
            val priority = AndroidDataSourcePriorityStore(context).load().token
            val pairing = TokenSyncStore(context).load()?.singleFlightIdentity().orEmpty()
            "$priority:${CredentialGeneration.current()}:$pairing"
        },
    )

    fun singleFlightIdentity(forceRefresh: Boolean): String =
        identity() + if (forceRefresh) ":force" else ":normal"

    fun read(forceRefresh: Boolean): TokenUsageSourceRead {
        val providers = when (priorityStore.load().token) {
            DataSourcePriority.OPENAI_FIRST -> listOf(hasOpenAI() to openAI, hasWindows() to windows)
            DataSourcePriority.WINDOWS_FIRST -> listOf(hasWindows() to windows, hasOpenAI() to openAI)
        }
        var firstFailure: Exception? = null
        var firstRetryableFailure: Exception? = null
        providers.forEach { (available, provider) ->
            if (!available) return@forEach
            try {
                return provider.read(forceRefresh)
            } catch (failure: Exception) {
                firstFailure = firstFailure ?: failure
                if (failure is TokenUsageException && failure.kind in RETRYABLE_TOKEN_FAILURES) {
                    firstRetryableFailure = firstRetryableFailure ?: failure
                }
            }
        }
        throw firstRetryableFailure ?: firstFailure ?: TokenUsageException(
            TokenUsageFailureKind.UNAVAILABLE,
            "尚未配置可用的 Token 数据来源",
        )
    }
}

private val RETRYABLE_TOKEN_FAILURES = setOf(
    TokenUsageFailureKind.OFFLINE,
    TokenUsageFailureKind.HTTP_ERROR,
    TokenUsageFailureKind.SERVER,
)

internal class WindowsTokenUsageProvider(
    private val pairingStore: TokenSyncPairingStore,
    private val transport: TokenUsageSyncTransport,
) : TokenUsageProvider {
    constructor(context: Context) : this(TokenSyncStore(context), TokenUsageSyncClient(context))

    override fun read(forceRefresh: Boolean): TokenUsageSourceRead {
        val pairing = pairingStore.load() ?: throw TokenUsageException(
            TokenUsageFailureKind.UNAVAILABLE,
            "尚未配对 Windows",
        )
        val result = transport.sync(pairing, forceRefresh)
        return TokenUsageSourceRead(
            snapshot = result.snapshot.copy(transport = DataTransport.WINDOWS),
            pairing = result.pairing,
            expectedPairing = pairing,
        )
    }
}

internal class OpenAIAccountTokenUsageProvider(
    private val credentialStore: OAuthStore,
    private val oauthClient: CodexOAuthClient,
    private val usageClient: CodexUsageClient,
) : TokenUsageProvider {
    constructor(context: Context) : this(
        OAuthStore(context),
        CodexOAuthClient(diagnostics = { AppLogStore.record(context, it) }),
        CodexUsageClient(),
    )

    override fun read(forceRefresh: Boolean): TokenUsageSourceRead {
        val generation = CredentialGeneration.current()
        var credentials = currentCredentials(generation)
        if (credentials.needsRefresh()) credentials = refresh(credentials, generation, allowStaleAccess = true)
        return try {
            retryUnauthorizedOnce(
                firstAttempt = { usageClient.fetchTokenProfile(credentials) },
                refresh = {
                    (if (credentials.refreshToken.isBlank()) {
                        currentCredentialsAfterUnauthorized(credentials, generation)
                    } else {
                        refresh(credentials, generation, allowStaleAccess = false)
                    }).also { credentials = it }
                },
                retry = { refreshed -> usageClient.fetchTokenProfile(refreshed) },
                onRepeatedUnauthorized = {
                synchronized(CodexProcessLock.monitor) {
                    if (CredentialGeneration.current() == generation && credentialStore.load() == credentials) {
                        credentialStore.clear()
                    }
                }
                },
            ).let { snapshot ->
                val expectedCredentials = credentials
                TokenUsageSourceRead(
                    snapshot = snapshot,
                    identityStillCurrent = {
                        CredentialGeneration.current() == generation && credentialStore.load() == expectedCredentials
                    },
                )
            }
        } catch (error: UsageException) {
            throw mapUsage(error)
        }
    }

    private fun currentCredentials(generation: Long): OAuthCredentials = synchronized(CodexProcessLock.monitor) {
        credentialStore.load()?.takeIf { CredentialGeneration.current() == generation }
            ?: throw TokenUsageException(TokenUsageFailureKind.LOGIN_REQUIRED, "尚未登录 Codex")
    }

    private fun currentCredentialsAfterUnauthorized(
        observed: OAuthCredentials,
        generation: Long,
    ): OAuthCredentials = synchronized(CodexProcessLock.monitor) {
        val current = credentialStore.load()
        if (CredentialGeneration.current() == generation && current != null && current != observed) {
            current
        } else {
            if (CredentialGeneration.current() == generation && current == observed) credentialStore.clear()
            throw TokenUsageException(TokenUsageFailureKind.LOGIN_REQUIRED, "登录已失效，请重新登录")
        }
    }

    private fun refresh(
        observed: OAuthCredentials,
        generation: Long,
        allowStaleAccess: Boolean,
    ): OAuthCredentials = try {
        ProcessCredentialRefreshCoordinator.instance.refresh(
            observed = observed,
            observedGeneration = generation,
            currentGeneration = CredentialGeneration::current,
            loadCurrent = credentialStore::load,
            performRefresh = { credentials ->
                oauthClient.refresh(
                    credentials,
                    if (allowStaleAccess) OAuthRefreshReason.PROACTIVE else OAuthRefreshReason.UNAUTHORIZED_RECOVERY,
                )
            },
            saveRefreshed = credentialStore::saveRefreshed,
            onFailure = { _, error ->
                if (error is OAuthException && error.kind in PERMANENT_AUTH_FAILURES) credentialStore.clear()
            },
        )
    } catch (error: CredentialRefreshAbortedException) {
        throw TokenUsageException(TokenUsageFailureKind.LOGIN_REQUIRED, "登录状态已改变，请重试")
    } catch (error: OAuthException) {
        when {
            error.kind in PERMANENT_AUTH_FAILURES -> throw TokenUsageException(TokenUsageFailureKind.LOGIN_REQUIRED, "登录已失效，请重新登录")
            allowStaleAccess && error.kind in setOf(OAuthFailureKind.NETWORK, OAuthFailureKind.SERVER) ->
                credentialStore.load() ?: throw TokenUsageException(TokenUsageFailureKind.LOGIN_REQUIRED, "登录状态已改变，请重试")
            error.kind == OAuthFailureKind.NETWORK -> throw TokenUsageException(TokenUsageFailureKind.OFFLINE, "无法连接认证服务")
            else -> throw TokenUsageException(TokenUsageFailureKind.SERVER, "认证服务暂时不可用")
        }
    }

    private fun mapUsage(error: UsageException): TokenUsageException = when (error.kind) {
        UsageFailureKind.UNAUTHORIZED -> TokenUsageException(TokenUsageFailureKind.LOGIN_REQUIRED, "登录已失效，请重新登录")
        UsageFailureKind.NETWORK -> TokenUsageException(TokenUsageFailureKind.OFFLINE, "无法连接 OpenAI Token 服务")
        UsageFailureKind.SERVER -> TokenUsageException(TokenUsageFailureKind.SERVER, "OpenAI Token 服务暂时不可用")
        UsageFailureKind.INVALID_RESPONSE -> TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "OpenAI Token 数据无法识别")
    }

    private companion object {
        val PERMANENT_AUTH_FAILURES = setOf(
            OAuthFailureKind.LOGIN_REQUIRED,
            OAuthFailureKind.REFRESH_EXPIRED,
            OAuthFailureKind.REFRESH_REVOKED,
            OAuthFailureKind.REFRESH_REUSED,
        )
    }
}

internal fun <T> retryUnauthorizedOnce(
    firstAttempt: () -> T,
    refresh: () -> OAuthCredentials,
    retry: (OAuthCredentials) -> T,
    onRepeatedUnauthorized: () -> Unit = {},
): T {
    try {
        return firstAttempt()
    } catch (error: UsageException) {
        if (error.kind != UsageFailureKind.UNAUTHORIZED) throw error
    }
    val refreshed = refresh()
    return try {
        retry(refreshed)
    } catch (error: UsageException) {
        if (error.kind == UsageFailureKind.UNAUTHORIZED) onRepeatedUnauthorized()
        throw error
    }
}
