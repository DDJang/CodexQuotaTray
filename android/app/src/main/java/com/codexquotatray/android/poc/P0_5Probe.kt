package com.codexquotatray.android.poc

import android.content.Context
import com.codexquotatray.android.BuildConfig
import com.codexquotatray.android.protocol.AppServerClient
import com.codexquotatray.android.protocol.ProtocolProbeResult
import com.codexquotatray.android.runtime.CodexProcess
import com.codexquotatray.android.runtime.EmbeddedCodexRuntime
import com.codexquotatray.android.runtime.ProcessCleanup
import com.codexquotatray.android.runtime.RuntimeStatus
import com.codexquotatray.android.runtime.VersionProbe

data class P0_5Result(
    val runtimePackaged: Boolean,
    val nativeLibraryPresent: Boolean,
    val runtimeReady: Boolean,
    val nativeLibraryDirectory: String,
    val codexExecutablePath: String,
    val codexExecutableExists: Boolean,
    val codexExecutableCanExecute: Boolean,
    val codexVersion: String,
    val appServerStarted: Boolean,
    val protocol: ProtocolProbeResult?,
    val processCleanup: ProcessCleanup,
    val lastError: String?,
    val success: Boolean,
    val recoveryAttempted: Boolean = false,
    val recoverySucceeded: Boolean = false,
) {
    fun render(): String = buildString {
        appendLine("Codex Android Runtime PoC")
        appendLine()
        appendLine("Runtime packaged: ${yesNo(runtimePackaged)}")
        appendLine("Native library present: ${yesNo(nativeLibraryPresent)}")
        appendLine("Runtime ready: ${yesNo(runtimeReady)}")
        appendLine("Native library dir: $nativeLibraryDirectory")
        appendLine("Codex executable path: $codexExecutablePath")
        appendLine("Codex executable exists: ${yesNo(codexExecutableExists)}")
        appendLine("Codex executable canExecute: ${yesNo(codexExecutableCanExecute)}")
        appendLine("Codex version: $codexVersion")
        appendLine("App Server started: ${yesNo(appServerStarted)}")
        appendLine(
            "Cleartext permitted for 127.0.0.1: ${
                protocol?.readyProbe?.cleartextPermitted?.let(::yesNo) ?: "not attempted"
            }",
        )
        appendLine("Ready probe: ${protocol?.readyProbe?.result ?: "not attempted"}")
        appendLine(
            "Ready probe /readyz HTTP status: ${
                protocol?.readyProbe?.httpStatusCode ?: "not attempted"
            }",
        )
        appendLine(
            "Ready probe /readyz HTTP message: ${
                protocol?.readyProbe?.httpStatusMessage ?: "none"
            }",
        )
        appendLine("WebSocket URL: ${protocol?.webSocket?.url ?: "not attempted"}")
        appendLine(
            "Origin header present: ${
                protocol?.webSocket?.originHeaderPresent?.let(::yesNo) ?: "not attempted"
            }",
        )
        appendLine(
            "WebSocket onOpen: ${
                protocol?.webSocket?.onOpen?.let(::yesNo) ?: "not attempted"
            }",
        )
        protocol?.webSocket?.failure?.let { failure ->
            appendLine("WebSocket failure class: ${failure.throwableClass}")
            appendLine("WebSocket failure message: ${failure.throwableMessage ?: "none"}")
            appendLine(
                "WebSocket cause: ${
                    failure.causeClass ?: "none"
                }: ${failure.causeMessage ?: "none"}",
            )
            appendLine(
                "WebSocket HTTP status: ${
                    failure.httpStatusCode ?: "none"
                } ${failure.httpStatusMessage ?: ""}".trim(),
            )
            appendLine("WebSocket upgrade headers: ${failure.upgradeHeaders ?: "none"}")
        }
        appendLine(
            "WebSocket onClosing: ${
                protocol?.webSocket?.let {
                    formatClose(it.onClosingCode, it.onClosingReason)
                } ?: "not attempted"
            }",
        )
        appendLine(
            "WebSocket onClosed: ${
                protocol?.webSocket?.let {
                    formatClose(it.onClosedCode, it.onClosedReason)
                } ?: "not attempted"
            }",
        )
        appendLine(
            "Initialize send attempted: ${
                protocol?.initialize?.sendAttempted?.let(::yesNo) ?: "not attempted"
            }",
        )
        appendLine(
            "Initialize send succeeded: ${
                protocol?.initialize?.sendSucceeded?.let(::yesNo) ?: "not attempted"
            }",
        )
        appendLine("Initialize response: ${protocol?.initialize?.response ?: "not attempted"}")
        appendLine("Initialize: ${protocol?.initializeSucceeded?.let(::successFail) ?: "not attempted"}")
        appendLine("Account: ${protocol?.accountResult ?: "not attempted"}")
        appendLine("Rate limits: ${protocol?.rateLimitsResult ?: "not attempted"}")
        appendLine("Rate limits read succeeded: ${yesNo(protocol?.rateLimitsReadSucceeded == true)}")
        appendLine("Quota window count: ${protocol?.quotaWindowCount ?: 0}")
        appendLine("Quota state (window data): ${protocol?.quotaState ?: "not_read"}")
        when (protocol?.authenticated) {
            true -> appendLine("Authentication: authenticated")
            false -> appendLine("Authentication required")
            null -> appendLine("Authentication: unknown")
        }
        appendLine("Authenticated: ${protocol?.authenticated?.toString() ?: "unknown"}")
        appendLine("Malformed WebSocket JSON: ${protocol?.malformedJsonCount ?: 0}")
        appendLine("stderr observed: ${yesNo(processCleanup.stderrObserved)}")
        appendLine("Process cleanup succeeded: ${yesNo(processCleanup.succeeded)}")
        appendLine("Process return code: ${processCleanup.returnCode ?: "unavailable"}")
        appendLine("Recovery attempted: ${yesNo(recoveryAttempted)}")
        appendLine("Recovery succeeded: ${yesNo(recoverySucceeded)}")
        appendLine("P0.5 flow completed: ${yesNo(protocol?.flowCompleted == true)}")
        appendLine("Success: ${yesNo(success)}")
        appendLine("Last error: ${lastError ?: "none"}")
    }

    private fun yesNo(value: Boolean): String = if (value) "yes" else "no"

    private fun successFail(value: Boolean): String = if (value) "success" else "fail"
}

data class P1Result(
    val runtimePackaged: Boolean,
    val nativeLibraryPresent: Boolean,
    val runtimeReady: Boolean,
    val nativeLibraryDirectory: String,
    val codexExecutablePath: String,
    val codexExecutableExists: Boolean,
    val codexExecutableCanExecute: Boolean,
    val codexVersion: String,
    val appServerStarted: Boolean,
    val login: com.codexquotatray.android.protocol.LoginProbeResult?,
    val processCleanup: ProcessCleanup,
    val lastError: String?,
    val success: Boolean,
) {
    fun render(): String = buildString {
        appendLine("Codex Android P1 Login")
        appendLine()
        appendLine("Runtime packaged: ${yesNo(runtimePackaged)}")
        appendLine("Native library present: ${yesNo(nativeLibraryPresent)}")
        appendLine("Runtime ready: ${yesNo(runtimeReady)}")
        appendLine("Native library dir: $nativeLibraryDirectory")
        appendLine("Codex executable path: $codexExecutablePath")
        appendLine("Codex executable exists: ${yesNo(codexExecutableExists)}")
        appendLine("Codex executable canExecute: ${yesNo(codexExecutableCanExecute)}")
        appendLine("Codex version: $codexVersion")
        appendLine("App Server started: ${yesNo(appServerStarted)}")
        appendLine("Ready probe: ${login?.readyProbe?.result ?: "not attempted"}")
        appendLine("WebSocket onOpen: ${login?.webSocket?.onOpen?.let(::yesNo) ?: "not attempted"}")
        appendLine("Initialize: ${login?.initializeSucceeded?.let(::successFail) ?: "not attempted"}")
        appendLine("Initial account/read: ${login?.initialAccountResult ?: "not attempted"}")
        appendLine("Login status: ${login?.loginState ?: "not attempted"}")
        appendLine("Login method: ${login?.loginMethod ?: "not attempted"}")
        login?.userCode?.let { appendLine("Device code: $it") }
        login?.verificationUrl?.let { appendLine("Verification URL: $it") }
        appendLine("Account/read: ${login?.accountResult ?: "not attempted"}")
        appendLine("Rate limits: ${login?.rateLimitsResult ?: "not attempted"}")
        appendLine("Rate limits read succeeded: ${yesNo(login?.rateLimitsReadSucceeded == true)}")
        appendLine("Quota window count: ${login?.quotaWindows?.size ?: 0}")
        appendLine("Quota state (window data): ${login?.quotaState ?: "not_read"}")
        login?.quotaWindows.orEmpty().forEachIndexed { index, window ->
            val label = window.limitName ?: window.sourceSlot
            appendLine(
                "Window ${index + 1} ($label): used=${window.usedPercent}%, " +
                    "remaining=${window.remainingPercent}%, " +
                    "duration=${window.windowDurationMins ?: "unknown"} mins, " +
                    "resetsAt=${window.resetsAt ?: "unknown"}",
            )
        }
        appendLine("Authenticated: ${login?.authenticated?.toString() ?: "unknown"}")
        appendLine("Login completion notification: ${yesNo(login?.loginNotificationReceived == true)}")
        appendLine("Malformed WebSocket JSON: ${login?.malformedJsonCount ?: 0}")
        appendLine("stderr observed: ${yesNo(processCleanup.stderrObserved)}")
        appendLine("Process cleanup succeeded: ${yesNo(processCleanup.succeeded)}")
        appendLine("Process return code: ${processCleanup.returnCode ?: "unavailable"}")
        appendLine("P1 flow completed: ${yesNo(success)}")
        appendLine("Success: ${yesNo(success)}")
        appendLine("Last error: ${lastError ?: "none"}")
    }

    private fun yesNo(value: Boolean): String = if (value) "yes" else "no"

    private fun successFail(value: Boolean): String = if (value) "success" else "fail"
}

class P0_5Probe(context: Context) {
    private val runtime = EmbeddedCodexRuntime(context.applicationContext)

    @Volatile
    private var process: CodexProcess? = null

    @Volatile
    private var client: AppServerClient? = null

    fun stop() {
        client?.stop()
        process?.stop()
    }

    fun run(): P0_5Result {
        val status = runtime.ensureReady()
        if (!status.ready) {
            return withoutProcess(
                status = status,
                version = "unavailable",
                error = status.detail,
            )
        }

        val version = runtime.probeVersion(status)
        if (!version.succeeded) {
            return withoutProcess(
                status = status,
                version = version.version,
                error = version.detail,
            )
        }

        val initial = runOnce(status, version)
        if (!shouldAttemptSimpleRecovery(initial)) return initial

        val recovered = runOnce(status, version)
        return recovered.copy(
            recoveryAttempted = true,
            recoverySucceeded = recovered.success,
        )
    }

    private fun runOnce(status: RuntimeStatus, version: VersionProbe): P0_5Result {

        var started = false
        var protocol: ProtocolProbeResult? = null
        var error: String? = null
        var cleanup = ProcessCleanup(false, null, false)
        try {
            val running = CodexProcess.start(runtime, status, BuildConfig.CODEX_APP_SERVER_PORT)
            process = running
            started = true
            val serverClient = AppServerClient(BuildConfig.CODEX_APP_SERVER_PORT)
            client = serverClient
            protocol = serverClient.runProbe()
        } catch (failure: Throwable) {
            error = "startup_failed: ${failure.javaClass.simpleName}: ${failure.processMessage()}"
        } finally {
            cleanup = process?.stop() ?: ProcessCleanup(true, null, false)
            process = null
            client = null
        }

        val protocolError = protocol?.lastError
        val lastError = error ?: protocolError
        val protocolSuccess = protocol?.let {
            it.readyProbe.succeeded &&
                it.webSocket.onOpen &&
                it.initializeSucceeded &&
                it.accountResult in setOf("succeeded", "unauthenticated", "unsupported", "rpc_error")
        } == true
        return P0_5Result(
            runtimePackaged = status.packaged,
            nativeLibraryPresent = status.nativeLibraryPresent,
            runtimeReady = status.ready,
            nativeLibraryDirectory = status.nativeLibraryDirectory.absolutePath,
            codexExecutablePath = status.codexBinary?.absolutePath ?: "unavailable",
            codexExecutableExists = status.executableExists,
            codexExecutableCanExecute = status.executableCanExecute,
            codexVersion = version.version,
            appServerStarted = started,
            protocol = protocol,
            processCleanup = cleanup,
            lastError = lastError,
            success = version.succeeded && started && protocolSuccess && cleanup.succeeded,
        )
    }

    fun runLogin(onUpdate: (com.codexquotatray.android.protocol.LoginUpdate) -> Unit = {}): P1Result {
        val status = runtime.ensureReady()
        if (!status.ready) {
            return withoutLoginProcess(status, "unavailable", status.detail)
        }

        val version = runtime.probeVersion(status)
        if (!version.succeeded) {
            return withoutLoginProcess(status, version.version, version.detail)
        }

        var started = false
        var login: com.codexquotatray.android.protocol.LoginProbeResult? = null
        var error: String? = null
        var cleanup = ProcessCleanup(false, null, false)
        try {
            val running = CodexProcess.start(runtime, status, BuildConfig.CODEX_APP_SERVER_PORT)
            process = running
            started = true
            val serverClient = AppServerClient(BuildConfig.CODEX_APP_SERVER_PORT)
            client = serverClient
            login = serverClient.runLogin(onUpdate = onUpdate)
        } catch (failure: Throwable) {
            error = "startup_failed: ${failure.javaClass.simpleName}: ${failure.processMessage()}"
        } finally {
            cleanup = process?.stop() ?: ProcessCleanup(true, null, false)
            process = null
            client = null
        }

        val lastError = error ?: login?.lastError
        val loginSuccess = login?.let {
            it.readyProbe.succeeded &&
                it.webSocket.onOpen &&
                it.initializeSucceeded &&
                it.loginState == "authenticated" &&
                it.authenticated == true &&
                it.accountResult == "succeeded" &&
                it.rateLimitsReadSucceeded
        } == true
        return P1Result(
            runtimePackaged = status.packaged,
            nativeLibraryPresent = status.nativeLibraryPresent,
            runtimeReady = status.ready,
            nativeLibraryDirectory = status.nativeLibraryDirectory.absolutePath,
            codexExecutablePath = status.codexBinary?.absolutePath ?: "unavailable",
            codexExecutableExists = status.executableExists,
            codexExecutableCanExecute = status.executableCanExecute,
            codexVersion = version.version,
            appServerStarted = started,
            login = login,
            processCleanup = cleanup,
            lastError = lastError,
            success = version.succeeded && started && loginSuccess && cleanup.succeeded,
        )
    }

    private fun withoutLoginProcess(status: RuntimeStatus, version: String, error: String): P1Result =
        P1Result(
            runtimePackaged = status.packaged,
            nativeLibraryPresent = status.nativeLibraryPresent,
            runtimeReady = status.ready,
            nativeLibraryDirectory = status.nativeLibraryDirectory.absolutePath,
            codexExecutablePath = status.codexBinary?.absolutePath ?: "unavailable",
            codexExecutableExists = status.executableExists,
            codexExecutableCanExecute = status.executableCanExecute,
            codexVersion = version,
            appServerStarted = false,
            login = null,
            processCleanup = ProcessCleanup(true, null, false),
            lastError = error,
            success = false,
        )

    private fun withoutProcess(status: RuntimeStatus, version: String, error: String): P0_5Result =
        P0_5Result(
            runtimePackaged = status.packaged,
            nativeLibraryPresent = status.nativeLibraryPresent,
            runtimeReady = status.ready,
            nativeLibraryDirectory = status.nativeLibraryDirectory.absolutePath,
            codexExecutablePath = status.codexBinary?.absolutePath ?: "unavailable",
            codexExecutableExists = status.executableExists,
            codexExecutableCanExecute = status.executableCanExecute,
            codexVersion = version,
            appServerStarted = false,
            protocol = null,
            processCleanup = ProcessCleanup(true, null, false),
            lastError = error,
            success = false,
        )
}

internal fun shouldAttemptSimpleRecovery(result: P0_5Result): Boolean {
    val protocol = result.protocol
    val error = protocol?.lastError ?: result.lastError.orEmpty()
    if (error.startsWith("startup_failed")) return true

    val readyFailure = protocol?.readyProbe?.let { probe ->
        !probe.succeeded && probe.result !in setOf("cleartext_not_permitted", "http_403")
    } == true
    if (readyFailure) return true

    return isRecoverableServerFailure(error)
}

internal fun isRecoverableServerFailure(error: String): Boolean = error in setOf(
    "connection_refused",
    "connection_timeout",
    "timeout",
    "other_ioexception",
    "websocket_open_failed",
    "websocket_closed_before_initialize",
    "initialize_timeout",
    "interrupted",
)

private fun Throwable.processMessage(): String =
    message?.replace(Regex("[\\r\\n]+"), " ")?.ifBlank { "no message" } ?: "no message"

private fun formatClose(code: Int?, reason: String?): String =
    code?.toString()?.let { codeText ->
        listOf(codeText, reason.orEmpty())
            .filter(String::isNotBlank)
            .joinToString(" ")
    } ?: "not observed"
