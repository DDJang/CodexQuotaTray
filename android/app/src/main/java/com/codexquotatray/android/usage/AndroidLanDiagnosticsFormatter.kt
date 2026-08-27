package com.codexquotatray.android.usage

import com.codexquotatray.android.AppLogSanitizer
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Pure, bounded formatter used by the Android pairing page and unit tests. */
internal object AndroidLanDiagnosticsFormatter {
    private const val MAX_EVENTS = 50
    private const val MAX_RECENT_EVENT_CHARS = 16_000

    fun format(
        version: String,
        pairing: TokenSyncPairing?,
        network: LanNetworkDiagnostics?,
        recentEvents: String,
        nowMillis: Long = System.currentTimeMillis(),
        epoch: LanNetworkEpochSnapshot = LanNetworkEpoch.snapshot(),
    ): String {
        val success = pairing?.lastLanSuccessAtMillis
        val endpoint = pairing?.lastLanTargetEndpoint ?: pairing?.let { "${it.host}:${it.port}" }
        val recent = recentEvents
            .lineSequence()
            .filter(String::isNotBlank)
            .map { line ->
                var safe = AppLogSanitizer.sanitizeLan(line)
                pairing?.pairingSecret?.takeIf(String::isNotBlank)?.let { secret ->
                    safe = safe.replace(secret, "[已隐藏]")
                }
                safe
            }
            .toList()
            .takeLast(MAX_EVENTS)
            .joinToString("\n")
            .takeLast(MAX_RECENT_EVENT_CHARS)
        val recentFields = recent.lineSequence()
            .filter { it.contains("socketBinding ") || it.contains("connectStart ") }
            .flatMap { line ->
                line.split(' ').mapNotNull { token ->
                    val separator = token.indexOf('=')
                    if (separator <= 0) null else token.substring(0, separator) to token.substring(separator + 1)
                }
            }
            .toMap()

        return buildString {
            appendLine("CodexQuotaTray LAN Diagnostics")
            appendLine()
            appendLine("App:")
            appendLine("version=$version")
            appendLine("platform=Android")
            appendLine("timestamp=${formatUtc(nowMillis)}")
            appendLine()
            appendLine("Pairing:")
            appendLine("paired=${pairing != null}")
            appendLine("device=${pairing?.deviceId?.takeIf(String::isNotBlank) ?: "unavailable"}")
            appendLine("endpoint=${endpoint ?: "unavailable"}")
            appendLine()
            appendLine("Last connection:")
            appendLine("lastSuccess=${success?.let(::formatUtc) ?: "unavailable"}")
            appendLine("lastFailure=${pairing?.lastLanFailureAtMillis?.let(::formatUtc) ?: "unavailable"}")
            appendLine("failurePhase=${pairing?.lastLanFailurePhase ?: "unavailable"}")
            appendLine("attempt=${pairing?.lastLanAttemptId?.toString() ?: "unavailable"}")
            appendLine("channel=${pairing?.lastLanAttemptChannel ?: "unavailable"}")
            appendLine("networkGeneration=${epoch.generation}")
            appendLine()
            appendLine("Network:")
            appendLine("networkHandle=${network?.networkHandle ?: "unavailable"}")
            appendLine("interface=${network?.interfaceName ?: "unavailable"}")
            appendLine("local=${network?.localIpv4?.let { value -> "${value}/${network.prefixLength ?: "?"}" } ?: "unavailable"}")
            appendLine("prefixLength=${network?.prefixLength ?: "unavailable"}")
            appendLine("gateway=${network?.gateway ?: "unavailable"}")
            appendLine("target=${endpoint ?: "unavailable"}")
            appendLine("route=${network?.routePrefix ?: "unavailable"}")
            appendLine("transports=${network?.transports?.takeIf { it.isNotEmpty() }?.joinToString(",") ?: "unavailable"}")
            appendLine("capabilities=${network?.capabilities?.takeIf { it.isNotEmpty() }?.joinToString(",") ?: "unavailable"}")
            appendLine("SSID=${network?.ssid ?: "unavailable"}")
            appendLine("BSSID=${network?.bssid ?: "unavailable"}")
            appendLine("frequency=${network?.frequencyMhz ?: "unavailable"}")
            appendLine("socketBoundToNetwork=${recentFields["boundToNetwork"] ?: "unavailable"}")
            appendLine("socketNetworkHandle=${recentFields["networkHandle"] ?: "unavailable"}")
            appendLine("socketNetworkGeneration=${recentFields["bindingGeneration"] ?: "unavailable"}")
            appendLine("connectNetworkGeneration=${recentFields["connectGeneration"] ?: "unavailable"}")
            appendLine("generationChangedDuringConnect=${recentFields["generationChanged"] ?: "unavailable"}")
            appendLine("neighborState=unavailable")
            appendLine("neighborCollection=unsupported_by_public_android_api")
            appendLine("lastNetworkChange=${epoch.lastNetworkChangeAtMillis?.let(::formatUtc) ?: "unavailable"}")
            appendLine("lastNetworkChangeReason=${epoch.lastNetworkChangeReason ?: "unavailable"}")
            appendLine("lastRecoveryAction=${epoch.lastRecoveryAction ?: "unavailable"}")
            appendLine()
            appendLine("Recent LAN events:")
            append(if (recent.isBlank()) "unavailable" else recent)
        }
    }

    /** Recovers only the last already-recorded route description for copy. */
    fun extractNetwork(recentEvents: String): LanNetworkDiagnostics? {
        val line = recentEvents.lineSequence()
            .filter { it.contains("route ") }
            .lastOrNull() ?: return null
        val fields = line.split(' ')
            .mapNotNull { token ->
                val separator = token.indexOf('=')
                if (separator <= 0) null else token.substring(0, separator) to token.substring(separator + 1)
            }
            .toMap()
        fun known(key: String): String? = fields[key]?.takeUnless { it.isBlank() || it == "unavailable" }
        val handle = known("networkHandle") ?: return null
        return LanNetworkDiagnostics(
            networkHandle = handle,
            interfaceName = known("interface"),
            localIpv4 = known("local"),
            prefixLength = known("prefixLength")?.toIntOrNull(),
            gateway = known("gateway"),
            routePrefix = known("routePrefix"),
            transports = known("transports")?.split(',').orEmpty(),
            capabilities = known("capabilities")?.split(',').orEmpty(),
            ssid = known("SSID"),
            bssid = known("BSSID"),
            frequencyMhz = known("frequency")?.toIntOrNull(),
        )
    }

    private fun formatUtc(millis: Long): String = SimpleDateFormat(
        "yyyy-MM-dd'T'HH:mm:ss.SSS'Z'",
        Locale.ROOT,
    ).apply { timeZone = java.util.TimeZone.getTimeZone("UTC") }.format(Date(millis))
}
