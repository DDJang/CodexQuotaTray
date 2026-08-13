package com.codexquotatray.android.update

enum class UpdateDownloadPhase {
    IDLE,
    DOWNLOADING,
    VERIFYING,
    CANCELLED,
    FAILED,
}

data class UpdateDownloadProgress(
    val phase: UpdateDownloadPhase,
    val bytesDownloaded: Long = 0L,
    val totalBytes: Long? = null,
    val bytesPerSecond: Double? = null,
) {
    val percentage: Int?
        get() = totalBytes?.takeIf { it > 0L }?.let {
            (bytesDownloaded * 100L / it).toInt().coerceIn(0, 100)
        }

    companion object {
        val Idle = UpdateDownloadProgress(UpdateDownloadPhase.IDLE)
    }
}
