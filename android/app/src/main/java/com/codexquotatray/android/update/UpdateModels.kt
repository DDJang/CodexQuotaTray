package com.codexquotatray.android.update

import java.util.Locale

/** Update backends are intentionally separate from the check/download policy. */
enum class UpdateSource(val label: String, val available: Boolean) {
    GITHUB("GitHub", true),
    GITEE("Gitee", false),
}

enum class UpdateCheckReason { AUTOMATIC, MANUAL }

data class SemVer(
    val major: Int,
    val minor: Int,
    val patch: Int,
) : Comparable<SemVer> {
    override fun compareTo(other: SemVer): Int = compareValuesBy(
        this,
        other,
        SemVer::major,
        SemVer::minor,
        SemVer::patch,
    )

    override fun toString(): String = "$major.$minor.$patch"

    companion object {
        private val STRICT = Regex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")

        fun parse(raw: String?): SemVer? {
            val match = raw?.trim()?.let(STRICT::matchEntire) ?: return null
            return runCatching {
                SemVer(
                    match.groupValues[1].toInt(),
                    match.groupValues[2].toInt(),
                    match.groupValues[3].toInt(),
                )
            }.getOrNull()
        }

        /** Accepts the current android-v tags and the historical v tags only. */
        fun parseAndroidTag(tag: String?): SemVer? {
            val normalized = tag?.trim()?.let {
                when {
                    it.startsWith("android-v", ignoreCase = true) -> it.substring(9)
                    it.startsWith("v", ignoreCase = true) -> it.substring(1)
                    else -> null
                }
            } ?: return null
            return parse(normalized)
        }

        fun parseReleaseTag(tag: String?): SemVer? {
            val normalized = tag?.trim()?.let {
                when {
                    it.startsWith("android-v", ignoreCase = true) -> it.substring(9)
                    it.startsWith("windows-v", ignoreCase = true) -> it.substring(9)
                    it.startsWith("v", ignoreCase = true) -> it.substring(1)
                    else -> null
                }
            } ?: return null
            return parse(normalized)
        }
    }
}

data class UpdateAsset(
    val name: String,
    val browserDownloadUrl: String,
    val sha256Digest: String? = null,
)

data class UpdateRelease(
    val tagName: String,
    val name: String,
    val notes: String,
    val publishedAt: String?,
    val version: SemVer,
    val androidAsset: UpdateAsset?,
)

sealed interface UpdateCheckResult {
    data class Available(val release: UpdateRelease, val currentVersion: SemVer) : UpdateCheckResult
    data class UpToDate(val currentVersion: SemVer, val latestVersion: SemVer? = null) : UpdateCheckResult
    data class NoAndroidAsset(val release: UpdateRelease) : UpdateCheckResult
    data class Skipped(val reason: SkipReason) : UpdateCheckResult
    data class Failed(val message: String, val cause: Throwable? = null) : UpdateCheckResult
}

enum class SkipReason { AUTO_DISABLED, WITHIN_INTERVAL, SOURCE_UNAVAILABLE, IN_FLIGHT }

class UpdateProviderException(message: String, cause: Throwable? = null) : Exception(message, cause)

interface UpdateProvider {
    val source: UpdateSource
    fun fetchLatest(): UpdateRelease
}

internal fun canonicalAndroidApkName(version: SemVer): String =
    "CodexQuotaTray-Android-v$version.apk"

internal fun isAndroidApkName(name: String, version: SemVer): Boolean {
    return name.trim() == canonicalAndroidApkName(version)
}

internal fun String.normalizedSha256(): String? {
    val value = trim().lowercase(Locale.ROOT)
    val hex = when {
        value.startsWith("sha256:") -> value.removePrefix("sha256:")
        value.length == 64 && value.all { it in "0123456789abcdef" } -> value
        else -> null
    } ?: return null
    return hex.takeIf { it.length == 64 && it.all { character -> character in "0123456789abcdef" } }
}
