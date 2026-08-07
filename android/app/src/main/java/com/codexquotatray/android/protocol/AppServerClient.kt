package com.codexquotatray.android.protocol

import android.security.NetworkSecurityPolicy
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.io.IOException
import java.net.ConnectException
import java.net.HttpURLConnection
import java.net.SocketTimeoutException
import java.net.URL
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

data class ReadyProbeResult(
    val cleartextPermitted: Boolean,
    val succeeded: Boolean,
    val result: String,
    val httpStatusCode: Int?,
    val httpStatusMessage: String?,
    val attempts: Int,
)

data class WebSocketFailureDiagnostic(
    val throwableClass: String,
    val throwableMessage: String?,
    val causeClass: String?,
    val causeMessage: String?,
    val httpStatusCode: Int?,
    val httpStatusMessage: String?,
    val upgradeHeaders: String?,
)

data class WebSocketDiagnostic(
    val url: String,
    val originHeaderPresent: Boolean,
    val onOpen: Boolean,
    val failure: WebSocketFailureDiagnostic?,
    val onClosingCode: Int?,
    val onClosingReason: String?,
    val onClosedCode: Int?,
    val onClosedReason: String?,
) {
    companion object {
        fun default(port: Int): WebSocketDiagnostic = WebSocketDiagnostic(
            url = "ws://127.0.0.1:$port",
            originHeaderPresent = false,
            onOpen = false,
            failure = null,
            onClosingCode = null,
            onClosingReason = null,
            onClosedCode = null,
            onClosedReason = null,
        )
    }
}

data class InitializeDiagnostic(
    val sendAttempted: Boolean,
    val sendSucceeded: Boolean?,
    val response: String,
)

data class QuotaWindow(
    val limitId: String?,
    val limitName: String?,
    val planType: String? = null,
    val sourceSlot: String,
    val usedPercent: Int,
    val remainingPercent: Int,
    val windowDurationMins: Long?,
    val resetsAt: Long?,
)

data class LoginUpdate(
    val state: String,
    val loginMethod: String? = null,
    val userCode: String? = null,
    val verificationUrl: String? = null,
    val authenticated: Boolean? = null,
    val accountResult: String? = null,
    val rateLimitsResult: String? = null,
    val quotaWindowCount: Int? = null,
    val quotaState: String? = null,
    val lastError: String? = null,
)

data class LoginProbeResult(
    val readyProbe: ReadyProbeResult,
    val webSocket: WebSocketDiagnostic,
    val initialize: InitializeDiagnostic,
    val initializeSucceeded: Boolean,
    val initialAccountResult: String,
    val accountResult: String,
    val rateLimitsResult: String,
    val rateLimitsReadSucceeded: Boolean,
    val quotaWindows: List<QuotaWindow>,
    val quotaState: String,
    val authenticated: Boolean?,
    val loginState: String,
    val loginMethod: String,
    val userCode: String?,
    val verificationUrl: String?,
    val loginNotificationReceived: Boolean,
    val malformedJsonCount: Int,
    val lastError: String?,
)

data class ProtocolProbeResult(
    val readyProbe: ReadyProbeResult,
    val webSocket: WebSocketDiagnostic,
    val initialize: InitializeDiagnostic,
    val initializeSucceeded: Boolean,
    val accountResult: String,
    val rateLimitsResult: String,
    val rateLimitsReadSucceeded: Boolean,
    val quotaWindowCount: Int,
    val quotaWindows: List<QuotaWindow>,
    val quotaState: String,
    val authenticated: Boolean?,
    val malformedJsonCount: Int,
    val flowCompleted: Boolean,
    val lastError: String?,
)

class AppServerClient(private val port: Int) {
    @Volatile
    private var activeSession: LiveSession? = null

    @Volatile
    private var activeClient: OkHttpClient? = null

    fun stop() {
        activeSession?.close()
        activeClient?.dispatcher?.executorService?.shutdownNow()
    }

    fun runProbe(totalTimeoutMillis: Long = 15_000L): ProtocolProbeResult {
        val client = OkHttpClient.Builder()
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build()
        activeClient = client
        var session: LiveSession? = null
        var readyProbe = ReadyProbeResult(
            cleartextPermitted = NetworkSecurityPolicy
                .getInstance()
                .isCleartextTrafficPermitted("127.0.0.1"),
            succeeded = false,
            result = "not_attempted",
            httpStatusCode = null,
            httpStatusMessage = null,
            attempts = 0,
        )
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(totalTimeoutMillis)
        val defaultWebSocket = WebSocketDiagnostic.default(port)
        val defaultInitialize = InitializeDiagnostic(false, null, "not_attempted")

        try {
            readyProbe = awaitReady(
                minOf(
                    deadline,
                    System.nanoTime() + TimeUnit.SECONDS.toNanos(5),
                ),
            )
            if (!readyProbe.succeeded) {
                return failedResult(
                    readyProbe = readyProbe,
                    webSocket = defaultWebSocket,
                    initialize = defaultInitialize,
                    lastError = readyProbe.result,
                )
            }

            val live = connect(client, deadline)
            session = live
            activeSession = live
            val initialize = live.request(
                requestId = 1L,
                method = "initialize",
                params = JSONObject()
                    .put(
                        "clientInfo",
                        JSONObject()
                            .put("name", "codexquotatray-android")
                            .put("title", "CodexQuota Android")
                            .put("version", "0.1.0"),
                    ),
                deadline = deadline,
            )
            val initializeDiagnostic = live.initializeDiagnostic()
            if (initialize.error != null) {
                return ProtocolProbeResult(
                    readyProbe = readyProbe,
                    webSocket = live.diagnostic(),
                    initialize = initializeDiagnostic,
                    initializeSucceeded = false,
                    accountResult = "not_attempted",
                    rateLimitsResult = "not_attempted",
                    rateLimitsReadSucceeded = false,
                    quotaWindowCount = 0,
                    quotaWindows = emptyList(),
                    quotaState = "not_read",
                    authenticated = if (initialize.error.isAuthenticationError()) false else null,
                    malformedJsonCount = live.malformedJsonCount,
                    flowCompleted = false,
                    lastError = if (initialize.error.isAuthenticationError()) {
                        "unauthenticated"
                    } else {
                        "initialize_rpc_error"
                    },
                )
            }
            if (!initialize.hasResult || initialize.result !is JSONObject) {
                return ProtocolProbeResult(
                    readyProbe = readyProbe,
                    webSocket = live.diagnostic(),
                    initialize = initializeDiagnostic,
                    initializeSucceeded = false,
                    accountResult = "not_attempted",
                    rateLimitsResult = "not_attempted",
                    rateLimitsReadSucceeded = false,
                    quotaWindowCount = 0,
                    quotaWindows = emptyList(),
                    quotaState = "not_read",
                    authenticated = null,
                    malformedJsonCount = live.malformedJsonCount,
                    flowCompleted = false,
                    lastError = "initialize_protocol_error",
                )
            }

            live.notify("initialized")
            var accountFailure: TransportFailure? = null
            val account = try {
                live.request(
                    requestId = 2L,
                    method = "account/read",
                    params = JSONObject().put("refreshToken", false),
                    deadline = deadline,
                )
            } catch (failure: TransportFailure) {
                accountFailure = failure
                null
            }
            val accountResult = account?.let(::classify) ?: "transport_error"
            var authenticated = account?.let(::authenticationFromAccount)

            var rateLimitsFailure: TransportFailure? = null
            val rateLimits = try {
                live.request(
                    requestId = 3L,
                    method = "account/rateLimits/read",
                    params = JSONObject.NULL,
                    deadline = deadline,
                )
            } catch (failure: TransportFailure) {
                rateLimitsFailure = failure
                null
            }
            val rateLimitsResult = rateLimits?.let(::classify) ?: "transport_error"
            val rateLimitsSucceeded = rateLimits != null && rateLimits.error == null &&
                rateLimits.hasResult && rateLimits.result is JSONObject
            val quota = if (rateLimitsSucceeded) {
                parseQuota(rateLimits.result)
            } else {
                QuotaSnapshot(emptyList(), "unavailable")
            }
            if (rateLimitsSucceeded) authenticated = true
            if (rateLimits?.error?.isAuthenticationError() == true) authenticated = false

            val transportFailure = accountFailure ?: rateLimitsFailure
            val transportError = transportFailure?.kind
            val lastError = when {
                transportError != null -> transportError
                rateLimitsResult == "succeeded" -> null
                rateLimitsResult == "unauthenticated" -> "unauthenticated"
                else -> "rate_limits_$rateLimitsResult"
            }
            val malformedCount = live.malformedJsonCount
            return ProtocolProbeResult(
                readyProbe = readyProbe,
                webSocket = transportFailure?.webSocket ?: live.diagnostic(),
                initialize = initializeDiagnostic,
                initializeSucceeded = true,
                accountResult = accountResult,
                rateLimitsResult = rateLimitsResult,
                rateLimitsReadSucceeded = rateLimitsSucceeded,
                quotaWindowCount = quota.windows.size,
                quotaWindows = quota.windows,
                quotaState = quota.state,
                authenticated = authenticated,
                malformedJsonCount = malformedCount,
                flowCompleted = account != null && rateLimits != null && malformedCount == 0,
                lastError = lastError,
            )
        } catch (failure: TransportFailure) {
            val webSocket = failure.webSocket ?: session?.diagnostic() ?: defaultWebSocket
            val initialize = session?.initializeDiagnostic() ?: defaultInitialize
            return ProtocolProbeResult(
                readyProbe = readyProbe,
                webSocket = webSocket,
                initialize = initialize,
                initializeSucceeded = session?.initializeSucceeded == true,
                accountResult = "not_attempted",
                rateLimitsResult = "not_attempted",
                rateLimitsReadSucceeded = false,
                quotaWindowCount = 0,
                quotaWindows = emptyList(),
                quotaState = "not_read",
                authenticated = null,
                malformedJsonCount = session?.malformedJsonCount ?: 0,
                flowCompleted = false,
                lastError = failure.kind,
            )
        } finally {
            session?.close()
            client.dispatcher.executorService.shutdown()
            client.connectionPool.evictAll()
            activeSession = null
            activeClient = null
        }
    }

    fun runLogin(
        totalTimeoutMillis: Long = TimeUnit.MINUTES.toMillis(5),
        onUpdate: (LoginUpdate) -> Unit = {},
    ): LoginProbeResult {
        val client = OkHttpClient.Builder()
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build()
        activeClient = client
        var session: LiveSession? = null
        var readyProbe = initialReadyProbe()
        var initialize = InitializeDiagnostic(false, null, "not_attempted")
        var initialAccountResult = "not_attempted"
        var accountResult = "not_attempted"
        var rateLimitsResult = "not_attempted"
        var rateLimitsReadSucceeded = false
        var quota = QuotaSnapshot(emptyList(), "not_read")
        var authenticated: Boolean? = null
        var loginState = "login_starting"
        var loginMethod = "not_attempted"
        var userCode: String? = null
        var verificationUrl: String? = null
        var loginNotificationReceived = false
        var lastError: String? = null
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(totalTimeoutMillis)
        val defaultWebSocket = WebSocketDiagnostic.default(port)

        fun result(
            error: String? = lastError,
            webSocket: WebSocketDiagnostic = session?.diagnostic() ?: defaultWebSocket,
        ): LoginProbeResult = LoginProbeResult(
            readyProbe = readyProbe,
            webSocket = webSocket,
            initialize = initialize,
            initializeSucceeded = session?.initializeSucceeded == true,
            initialAccountResult = initialAccountResult,
            accountResult = accountResult,
            rateLimitsResult = rateLimitsResult,
            rateLimitsReadSucceeded = rateLimitsReadSucceeded,
            quotaWindows = quota.windows,
            quotaState = quota.state,
            authenticated = authenticated,
            loginState = loginState,
            loginMethod = loginMethod,
            userCode = userCode,
            verificationUrl = verificationUrl,
            loginNotificationReceived = loginNotificationReceived,
            malformedJsonCount = session?.malformedJsonCount ?: 0,
            lastError = error,
        )

        try {
            readyProbe = awaitReady(
                minOf(
                    deadline,
                    System.nanoTime() + TimeUnit.SECONDS.toNanos(5),
                ),
            )
            if (!readyProbe.succeeded) {
                loginState = "login_failed"
                lastError = readyProbe.result
                onUpdate(LoginUpdate(loginState, lastError = lastError))
                return result()
            }

            val live = connect(client, deadline)
            session = live
            activeSession = live

            val initializeResponse = live.request(
                requestId = 1L,
                method = "initialize",
                params = initializeParams(),
                deadline = deadline,
            )
            initialize = live.initializeDiagnostic()
            if (initializeResponse.error != null) {
                loginState = "login_failed"
                lastError = if (initializeResponse.error.isAuthenticationError()) {
                    "unauthenticated"
                } else {
                    "initialize_rpc_error"
                }
                onUpdate(LoginUpdate(loginState, lastError = lastError))
                return result()
            }
            if (!initializeResponse.hasResult || initializeResponse.result !is JSONObject) {
                loginState = "login_failed"
                lastError = "initialize_protocol_error"
                onUpdate(LoginUpdate(loginState, lastError = lastError))
                return result()
            }
            live.notify("initialized")

            val initialAccount = live.request(
                requestId = 2L,
                method = "account/read",
                params = JSONObject().put("refreshToken", false),
                deadline = deadline,
            )
            initialAccountResult = classify(initialAccount)
            authenticated = authenticationFromAccount(initialAccount)
            if (authenticated == true) {
                accountResult = initialAccountResult
                loginState = "authenticated"
                onUpdate(
                    LoginUpdate(
                        state = loginState,
                        authenticated = true,
                        accountResult = accountResult,
                    ),
                )
            } else {
                authenticated = false
                onUpdate(
                    LoginUpdate(
                        state = "unauthenticated",
                        authenticated = false,
                        accountResult = initialAccountResult,
                    ),
                )
                onUpdate(LoginUpdate(state = "login_starting", loginMethod = "device_code"))
                loginMethod = "device_code"
                var nextRequestId = 5L
                var loginResponse = live.request(
                    requestId = 4L,
                    method = "account/login/start",
                    params = JSONObject().put("type", "chatgptDeviceCode"),
                    deadline = deadline,
                )
                if (loginResponse.error != null) {
                    loginMethod = "browser_fallback"
                    onUpdate(LoginUpdate(state = "login_starting", loginMethod = loginMethod))
                    loginResponse = live.request(
                        requestId = nextRequestId++,
                        method = "account/login/start",
                        params = JSONObject().put("type", "chatgpt"),
                        deadline = deadline,
                    )
                }
                if (loginResponse.error != null) {
                    loginState = "login_failed"
                    lastError = "login_start_rpc_error"
                    onUpdate(LoginUpdate(loginState, loginMethod = loginMethod, lastError = lastError))
                    return result()
                }
                val loginPayload = loginResponse.result as? JSONObject
                loginMethod = when (loginPayload?.stringOrNull("type")) {
                    "chatgptDeviceCode" -> {
                        userCode = loginPayload.stringOrNull("userCode")
                        verificationUrl = loginPayload.stringOrNull("verificationUrl")
                        "device_code"
                    }
                    "chatgpt" -> {
                        userCode = null
                        verificationUrl = loginPayload.stringOrNull("authUrl")
                        "browser"
                    }
                    else -> "unknown"
                }
                if (loginMethod == "unknown" || verificationUrl.isNullOrBlank() ||
                    (loginMethod == "device_code" && userCode.isNullOrBlank())
                ) {
                    loginState = "login_failed"
                    lastError = "login_protocol_error"
                    onUpdate(LoginUpdate(loginState, loginMethod = loginMethod, lastError = lastError))
                    return result()
                }

                loginState = "waiting_for_user"
                onUpdate(
                    LoginUpdate(
                        state = loginState,
                        loginMethod = loginMethod,
                        userCode = userCode,
                        verificationUrl = verificationUrl,
                        authenticated = false,
                        accountResult = initialAccountResult,
                    ),
                )

                var nextAccountPoll = System.nanoTime()
                var postLoginAccount: RpcResponse? = null
                while (remainingMillis(deadline) > 0L) {
                    val completed = live.pollLoginCompleted(
                        minOf(250L, remainingMillis(deadline)),
                    )
                    if (completed != null) {
                        loginNotificationReceived = true
                        if (!completed.success) {
                            loginState = "login_failed"
                            lastError = "login_failed"
                            onUpdate(LoginUpdate(loginState, lastError = lastError))
                            return result()
                        }
                        postLoginAccount = live.request(
                            requestId = nextRequestId++,
                            method = "account/read",
                            params = accountReadParams(refreshToken = true).toJson(),
                            deadline = deadline,
                        )
                        break
                    }

                    if (System.nanoTime() >= nextAccountPoll) {
                        postLoginAccount = live.request(
                            requestId = nextRequestId++,
                            method = "account/read",
                            params = JSONObject().put("refreshToken", false),
                            deadline = minOf(
                                deadline,
                                System.nanoTime() + TimeUnit.SECONDS.toNanos(2),
                            ),
                        )
                        if (authenticationFromAccount(postLoginAccount) == true) break
                        postLoginAccount = null
                        nextAccountPoll = System.nanoTime() + TimeUnit.SECONDS.toNanos(1)
                    }
                }

                if (postLoginAccount == null) {
                    loginState = "login_failed"
                    lastError = "login_timeout"
                    onUpdate(LoginUpdate(loginState, lastError = lastError))
                    return result()
                }
                accountResult = classify(postLoginAccount)
                authenticated = authenticationFromAccount(postLoginAccount)
                if (authenticated != true) {
                    loginState = "login_failed"
                    lastError = "login_not_authenticated"
                    onUpdate(
                        LoginUpdate(
                            state = loginState,
                            authenticated = authenticated,
                            accountResult = accountResult,
                            lastError = lastError,
                        ),
                    )
                    return result()
                }
                loginState = "authenticated"
                onUpdate(
                    LoginUpdate(
                        state = loginState,
                        authenticated = true,
                        accountResult = accountResult,
                    ),
                )
            }

            val rateLimits = live.request(
                requestId = if (authenticated == true && initialAccountResult == accountResult) 3L else 100L,
                method = "account/rateLimits/read",
                params = JSONObject.NULL,
                deadline = deadline,
            )
            rateLimitsResult = classify(rateLimits)
            rateLimitsReadSucceeded = rateLimits.error == null &&
                rateLimits.hasResult && rateLimits.result is JSONObject
            quota = if (rateLimitsReadSucceeded) {
                parseQuota(rateLimits.result)
            } else {
                QuotaSnapshot(emptyList(), "unavailable")
            }
            if (rateLimits.error?.isAuthenticationError() == true) authenticated = false
            lastError = when {
                rateLimitsResult == "succeeded" -> null
                rateLimitsResult == "unauthenticated" -> "unauthenticated"
                else -> "rate_limits_$rateLimitsResult"
            }
            onUpdate(
                LoginUpdate(
                    state = loginState,
                    authenticated = authenticated,
                    accountResult = accountResult,
                    rateLimitsResult = rateLimitsResult,
                    quotaWindowCount = quota.windows.size,
                    quotaState = quota.state,
                    lastError = lastError,
                ),
            )
            return result()
        } catch (failure: TransportFailure) {
            loginState = "login_failed"
            lastError = failure.kind
            onUpdate(LoginUpdate(loginState, lastError = lastError))
            return result(error = lastError, webSocket = failure.webSocket ?: session?.diagnostic() ?: defaultWebSocket)
        } finally {
            session?.close()
            client.dispatcher.executorService.shutdown()
            client.connectionPool.evictAll()
            activeSession = null
            activeClient = null
        }
    }

    private fun awaitReady(deadline: Long): ReadyProbeResult {
        val cleartextPermitted = NetworkSecurityPolicy
            .getInstance()
            .isCleartextTrafficPermitted("127.0.0.1")
        if (!cleartextPermitted) {
            return ReadyProbeResult(
                cleartextPermitted = false,
                succeeded = false,
                result = "cleartext_not_permitted",
                httpStatusCode = null,
                httpStatusMessage = null,
                attempts = 0,
            )
        }

        var attempts = 0
        var lastStatusCode: Int? = null
        var lastStatusMessage: String? = null
        var lastError = "connection_timeout"
        while (remainingMillis(deadline) > 0L) {
            attempts++
            var connection: HttpURLConnection? = null
            try {
                connection = URL("http://127.0.0.1:$port/readyz").openConnection() as HttpURLConnection
                val timeout = minOf(500L, remainingMillis(deadline)).toInt().coerceAtLeast(1)
                connection.connectTimeout = timeout
                connection.readTimeout = timeout
                connection.requestMethod = "GET"
                connection.useCaches = false
                val statusCode = connection.responseCode
                val statusMessage = safeText(connection.responseMessage)
                lastStatusCode = statusCode
                lastStatusMessage = statusMessage
                if (statusCode == HttpURLConnection.HTTP_OK) {
                    return ReadyProbeResult(
                        cleartextPermitted = true,
                        succeeded = true,
                        result = "ready",
                        httpStatusCode = statusCode,
                        httpStatusMessage = statusMessage,
                        attempts = attempts,
                    )
                }
                lastError = if (statusCode == HttpURLConnection.HTTP_FORBIDDEN) {
                    "http_403"
                } else {
                    "ready_http_$statusCode"
                }
                if (statusCode == HttpURLConnection.HTTP_FORBIDDEN) break
            } catch (failure: Throwable) {
                lastError = classifyNetworkFailure(failure)
                if (lastError == "cleartext_not_permitted") break
            } finally {
                connection?.disconnect()
            }
            val delay = minOf(150L, remainingMillis(deadline))
            if (delay > 0L) Thread.sleep(delay)
        }
        return ReadyProbeResult(
            cleartextPermitted = true,
            succeeded = false,
            result = lastError,
            httpStatusCode = lastStatusCode,
            httpStatusMessage = lastStatusMessage,
            attempts = attempts,
        )
    }

    private fun initialReadyProbe(): ReadyProbeResult = ReadyProbeResult(
        cleartextPermitted = NetworkSecurityPolicy
            .getInstance()
            .isCleartextTrafficPermitted("127.0.0.1"),
        succeeded = false,
        result = "not_attempted",
        httpStatusCode = null,
        httpStatusMessage = null,
        attempts = 0,
    )

    private fun initializeParams(): JSONObject = JSONObject()
        .put(
            "clientInfo",
            JSONObject()
                .put("name", "codexquotatray-android")
                .put("title", "CodexQuota Android")
                .put("version", "0.1.0"),
        )

    private fun connect(client: OkHttpClient, deadline: Long): LiveSession {
        var lastFailure = "websocket_open_failed"
        var lastDiagnostic = WebSocketDiagnostic.default(port)
        repeat(20) {
            val remaining = remainingMillis(deadline)
            if (remaining <= 0L) {
                throw TransportFailure("connection_timeout", lastDiagnostic)
            }
            val candidate = LiveSession(client, port)
            if (candidate.awaitOpen(minOf(remaining, 750L))) return candidate
            lastDiagnostic = candidate.diagnostic()
            lastFailure = classifyWebSocketFailure(lastDiagnostic)
            candidate.close()
            if (lastFailure == "cleartext_not_permitted" || lastFailure.startsWith("http_")) {
                throw TransportFailure(lastFailure, lastDiagnostic)
            }
            val delay = minOf(100L, remainingMillis(deadline))
            if (delay > 0L) Thread.sleep(delay)
        }
        throw TransportFailure(lastFailure, lastDiagnostic)
    }

    private fun classifyWebSocketFailure(diagnostic: WebSocketDiagnostic): String {
        val failure = diagnostic.failure
        if (failure?.httpStatusCode != null) return "http_" + failure.httpStatusCode
        val text = listOf(
            failure?.throwableClass,
            failure?.throwableMessage,
            failure?.causeClass,
            failure?.causeMessage,
        ).filterNotNull().joinToString(" ").lowercase()
        return when {
            text.contains("cleartext") -> "cleartext_not_permitted"
            text.contains("connection refused") || text.contains("econnrefused") ||
                text.contains("failed to connect") -> "connection_refused"
            text.contains("timeout") -> "connection_timeout"
            diagnostic.onClosedCode != null && !diagnostic.onOpen ->
                "websocket_closed_before_initialize"
            failure?.throwableClass?.endsWith("IOException") == true ||
                failure?.throwableClass == "SocketException" -> "other_ioexception"
            failure != null -> "websocket_open_failed"
            else -> "connection_timeout"
        }
    }

    private fun classify(response: RpcResponse): String = when {
        response.error?.isAuthenticationError() == true -> "unauthenticated"
        response.error?.code == -32601 -> "unsupported"
        response.error != null -> "rpc_error"
        !response.hasResult -> "protocol_error"
        else -> "succeeded"
    }

    private fun authenticationFromAccount(response: RpcResponse): Boolean? {
        if (response.error != null) return response.error.isAuthenticationError().takeIf { it }
        val result = response.result as? JSONObject ?: return null
        if (result.has("requiresOpenaiAuth")) return !result.optBoolean("requiresOpenaiAuth")
        val authMode = result.optString("authMode").lowercase()
        return when {
            authMode.isBlank() -> null
            authMode in setOf("none", "unauthenticated") -> false
            else -> true
        }
    }

    private fun parseQuota(value: Any?): QuotaSnapshot {
        val result = value as? JSONObject ?: return QuotaSnapshot(emptyList(), "unavailable")
        val buckets = result.opt("rateLimitsByLimitId")
        val snapshots = mutableListOf<SnapshotEntry>()
        var incomplete = false

        if (buckets is JSONObject) {
            if (buckets.length() == 0) return QuotaSnapshot(emptyList(), "zero_windows")
            for (key in buckets.keys()) {
                val snapshot = buckets.opt(key)
                if (snapshot is JSONObject) {
                    snapshots += SnapshotEntry(key, snapshot)
                } else {
                    incomplete = true
                }
            }
        } else if (result.has("rateLimits")) {
            val legacy = result.opt("rateLimits")
            if (legacy is JSONObject) {
                snapshots += SnapshotEntry(null, legacy)
            } else {
                incomplete = true
            }
        } else {
            return QuotaSnapshot(emptyList(), "unavailable")
        }

        val windows = mutableListOf<QuotaWindow>()
        for ((bucketId, snapshot) in snapshots) {
            val limitId = snapshot.stringOrNull("limitId") ?: bucketId
            val limitName = snapshot.stringOrNull("limitName")
            val planType = snapshot.stringOrNull("planType")
            var sawWindow = false
            for (slot in snapshot.keys()) {
                val valueAtSlot = snapshot.opt(slot)
                if (valueAtSlot !is JSONObject) continue
                val looksLikeWindow = valueAtSlot.has("usedPercent") ||
                    valueAtSlot.has("windowDurationMins") ||
                    valueAtSlot.has("resetsAt")
                if (!looksLikeWindow) continue
                sawWindow = true
                val used = (valueAtSlot.opt("usedPercent") as? Number)?.toInt()
                if (used == null) {
                    incomplete = true
                    continue
                }
                val clampedUsed = used.coerceIn(0, 100)
                windows += QuotaWindow(
                    limitId = limitId,
                    limitName = limitName,
                    planType = planType,
                    sourceSlot = slot,
                    usedPercent = clampedUsed,
                    remainingPercent = 100 - clampedUsed,
                    windowDurationMins = (valueAtSlot.opt("windowDurationMins") as? Number)?.toLong(),
                    resetsAt = (valueAtSlot.opt("resetsAt") as? Number)?.toLong(),
                )
            }
            if (snapshot.length() > 0 && !sawWindow) incomplete = true
        }
        val state = when {
            incomplete -> "unavailable"
            windows.isEmpty() -> "zero_windows"
            else -> "available"
        }
        return QuotaSnapshot(windows, state)
    }

    private fun failedResult(
        readyProbe: ReadyProbeResult,
        webSocket: WebSocketDiagnostic,
        initialize: InitializeDiagnostic,
        lastError: String,
    ): ProtocolProbeResult = ProtocolProbeResult(
        readyProbe = readyProbe,
        webSocket = webSocket,
        initialize = initialize,
        initializeSucceeded = false,
        accountResult = "not_attempted",
        rateLimitsResult = "not_attempted",
        rateLimitsReadSucceeded = false,
        quotaWindowCount = 0,
        quotaWindows = emptyList(),
        quotaState = "not_read",
        authenticated = null,
        malformedJsonCount = 0,
        flowCompleted = false,
        lastError = lastError,
    )

    private fun remainingMillis(deadline: Long): Long =
        TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime()).coerceAtLeast(0L)
}

private data class SnapshotEntry(val bucketId: String?, val snapshot: JSONObject)

private data class QuotaSnapshot(val windows: List<QuotaWindow>, val state: String)

private data class RpcError(val code: Int?, val message: String) {
    fun isAuthenticationError(): Boolean {
        if (code in setOf(401, 403, -32001, -32002)) return true
        val value = message.lowercase()
        return listOf(
            "unauthoriz",
            "unauthent",
            "not logged",
            "login required",
            "sign in",
            "credential",
            "access token",
            "refresh token",
            "forbidden",
        ).any(value::contains)
    }
}

private data class RpcResponse(
    val hasResult: Boolean,
    val result: Any?,
    val error: RpcError?,
)

internal data class AccountReadParams(val refreshToken: Boolean) {
    fun toJson(): JSONObject = JSONObject().put("refreshToken", refreshToken)
}

internal fun accountReadParams(refreshToken: Boolean): AccountReadParams =
    AccountReadParams(refreshToken)

private class TransportFailure(
    val kind: String,
    val webSocket: WebSocketDiagnostic? = null,
) : Exception(kind)

private data class LoginCompletion(val success: Boolean)

private class LiveSession(private val client: OkHttpClient, private val port: Int) {
    private companion object {
        const val STOP_SENTINEL = "__codexquotatray_p0_5_stop__"
    }

    private val open = CountDownLatch(1)
    private val messages = LinkedBlockingQueue<String>()
    private val notifications = LinkedBlockingQueue<JSONObject>()
    private val failure = AtomicReference<WebSocketFailureDiagnostic?>(null)
    private val opened = AtomicBoolean(false)
    private val requestUrl = "ws://127.0.0.1:$port"
    private val request: Request = Request.Builder()
        .url(requestUrl)
        .build()
    private val originHeaderPresent = request.header("Origin") != null
    private var socket: WebSocket? = null

    @Volatile
    private var onClosingCode: Int? = null

    @Volatile
    private var onClosingReason: String? = null

    @Volatile
    private var onClosedCode: Int? = null

    @Volatile
    private var onClosedReason: String? = null

    @Volatile
    var initializeSendAttempted: Boolean = false
        private set

    @Volatile
    var initializeSendSucceeded: Boolean? = null
        private set

    @Volatile
    var initializeResponse: String = "not_received"
        private set

    @Volatile
    var initializeSucceeded: Boolean = false
        private set

    var malformedJsonCount: Int = 0
        private set

    private val listener = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            opened.set(true)
            open.countDown()
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            messages.offer(text)
        }

        override fun onFailure(webSocket: WebSocket, throwable: Throwable, response: Response?) {
            failure.compareAndSet(null, failureDiagnostic(throwable, response))
            open.countDown()
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            onClosingCode = code
            onClosingReason = safeText(reason)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            onClosedCode = code
            onClosedReason = safeText(reason)
            if (!opened.get()) {
                failure.compareAndSet(
                    null,
                    WebSocketFailureDiagnostic(
                        throwableClass = "WebSocketClosed",
                        throwableMessage = safeText(reason),
                        causeClass = null,
                        causeMessage = null,
                        httpStatusCode = null,
                        httpStatusMessage = null,
                        upgradeHeaders = null,
                    ),
                )
            }
            open.countDown()
        }
    }

    init {
        socket = client.newWebSocket(request, listener)
    }

    fun awaitOpen(timeoutMillis: Long): Boolean =
        open.await(timeoutMillis, TimeUnit.MILLISECONDS) && opened.get() && failure.get() == null

    fun diagnostic(): WebSocketDiagnostic = WebSocketDiagnostic(
        url = requestUrl,
        originHeaderPresent = originHeaderPresent,
        onOpen = opened.get(),
        failure = failure.get(),
        onClosingCode = onClosingCode,
        onClosingReason = onClosingReason,
        onClosedCode = onClosedCode,
        onClosedReason = onClosedReason,
    )

    fun initializeDiagnostic(): InitializeDiagnostic = InitializeDiagnostic(
        sendAttempted = initializeSendAttempted,
        sendSucceeded = initializeSendSucceeded,
        response = initializeResponse,
    )

    fun notify(method: String) {
        if (socket?.send(JSONObject().put("method", method).toString()) != true) {
            throw TransportFailure("websocket_open_failed", diagnostic())
        }
    }

    fun pollLoginCompleted(timeoutMillis: Long): LoginCompletion? {
        while (true) {
            val message = notifications.poll(timeoutMillis, TimeUnit.MILLISECONDS) ?: return null
            if (message.optString("method") != "account/login/completed") continue
            val params = message.optJSONObject("params")
                ?: return LoginCompletion(success = false)
            return LoginCompletion(success = params.optBoolean("success", false))
        }
    }

    fun request(requestId: Long, method: String, params: Any?, deadline: Long): RpcResponse {
        val message = JSONObject()
            .put("id", requestId)
            .put("method", method)
            .put("params", params)
        if (requestId == 1L) initializeSendAttempted = true
        val sent = socket?.send(message.toString()) == true
        if (requestId == 1L) initializeSendSucceeded = sent
        if (!sent) {
            throw TransportFailure("websocket_open_failed", diagnostic())
        }

        while (true) {
            val remaining = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime())
            if (remaining <= 0L) {
                throw TransportFailure(
                    if (requestId == 1L) "initialize_timeout" else "timeout",
                    diagnostic(),
                )
            }
            val raw = messages.poll(minOf(remaining, 250L), TimeUnit.MILLISECONDS)
            if (raw == null) {
                if (failure.get() != null || onClosedCode != null) {
                    throw TransportFailure(classifyRequestFailure(requestId, diagnostic()), diagnostic())
                }
                continue
            }
            if (raw == STOP_SENTINEL) throw TransportFailure("interrupted", diagnostic())
            for (line in raw.split('\n')) {
                val text = line.trim()
                if (text.isEmpty()) continue
                val json = runCatching { JSONObject(text) }.getOrNull()
                if (json == null) {
                    malformedJsonCount++
                    continue
                }
                if (json.optString("method").isNotBlank() && !json.has("id")) {
                    notifications.offer(json)
                    continue
                }
                val responseId = json.opt("id")
                if (responseId !is Number || responseId.toLong() != requestId) continue
                val error = json.optJSONObject("error")?.let { node ->
                    val code = (node.opt("code") as? Number)?.toInt()
                    RpcError(code, node.optString("message"))
                }
                val hasResult = json.has("result")
                val result = if (hasResult) json.opt("result") else null
                if (requestId == 1L) {
                    initializeResponse = when {
                        error != null -> if (error.isAuthenticationError()) "unauthenticated" else "rpc_error"
                        !hasResult || result !is JSONObject -> "protocol_error"
                        else -> "success"
                    }
                    initializeSucceeded = error == null && hasResult && result is JSONObject
                }
                return RpcResponse(hasResult, result, error)
            }
        }
    }

    fun close() {
        socket?.close(1000, "p0.5 done")
        socket?.cancel()
        messages.offer(STOP_SENTINEL)
    }
}

private fun failureDiagnostic(
    throwable: Throwable,
    response: Response?,
): WebSocketFailureDiagnostic = WebSocketFailureDiagnostic(
    throwableClass = throwable.javaClass.simpleName,
    throwableMessage = safeText(throwable.message),
    causeClass = throwable.cause?.javaClass?.simpleName,
    causeMessage = safeText(throwable.cause?.message),
    httpStatusCode = response?.code,
    httpStatusMessage = safeText(response?.message),
    upgradeHeaders = response?.let(::upgradeHeaders),
)

private fun upgradeHeaders(response: Response): String? {
    val names = response.headers.names()
        .filter { it.equals("Upgrade", ignoreCase = true) || it.equals("Connection", ignoreCase = true) }
        .sorted()
    if (names.isEmpty()) return null
    return names.joinToString("; ") { name ->
        name + "=" + response.headers.values(name).joinToString(",") { safeText(it) ?: "" }
    }
}

private fun classifyNetworkFailure(throwable: Throwable): String {
    val text = listOf(
        throwable.javaClass.simpleName,
        throwable.message,
        throwable.cause?.javaClass?.simpleName,
        throwable.cause?.message,
    ).filterNotNull().joinToString(" ").lowercase()
    return when {
        text.contains("cleartext") -> "cleartext_not_permitted"
        throwable is ConnectException || text.contains("connection refused") ||
            text.contains("econnrefused") -> "connection_refused"
        throwable is SocketTimeoutException || text.contains("timeout") -> "connection_timeout"
        throwable is IOException -> "other_ioexception"
        else -> "ready_probe_error"
    }
}

private fun classifyRequestFailure(
    requestId: Long,
    diagnostic: WebSocketDiagnostic,
): String = when {
    requestId == 1L && !diagnostic.onOpen -> "websocket_closed_before_initialize"
    requestId == 1L && diagnostic.onClosedCode != null -> "websocket_open_failed"
    requestId == 1L -> "initialize_timeout"
    diagnostic.onClosedCode != null -> "websocket_open_failed"
    else -> "timeout"
}

private fun safeText(value: String?): String? {
    if (value == null) return null
    return value
        .replace(Regex("[\\r\\n\\t]+"), " ")
        .trim()
        .take(240)
        .ifBlank { null }
}

private fun JSONObject.stringOrNull(name: String): String? =
    opt(name).takeIf { it is String } as String?
