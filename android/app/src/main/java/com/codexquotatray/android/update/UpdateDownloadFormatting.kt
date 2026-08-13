package com.codexquotatray.android.update

import java.util.Locale

object UpdateDownloadFormatting {
    fun formatBytes(bytes: Long): String {
        if (bytes < 1024L) return "$bytes B"
        var value = bytes / 1024.0
        if (value < 1024.0) return String.format(Locale.ROOT, "%.1f KB", value)
        value /= 1024.0
        if (value < 1024.0) return String.format(Locale.ROOT, "%.1f MB", value)
        return String.format(Locale.ROOT, "%.1f GB", value / 1024.0)
    }

    fun formatSpeed(bytesPerSecond: Double): String {
        val megabytesPerSecond = bytesPerSecond / (1024.0 * 1024.0)
        return if (megabytesPerSecond < 1.0) {
            String.format(Locale.ROOT, "%.2f MB/s", megabytesPerSecond)
        } else {
            String.format(Locale.ROOT, "%.1f MB/s", megabytesPerSecond)
        }
    }

    fun formatProgress(progress: UpdateDownloadProgress): String {
        val downloaded = formatBytes(progress.bytesDownloaded)
        val size = progress.totalBytes?.takeIf { it > 0L }?.let {
            "$downloaded / ${formatBytes(it)}"
        } ?: downloaded
        return progress.bytesPerSecond?.takeIf { it > 0.0 }?.let {
            "$size · ${formatSpeed(it)}"
        } ?: size
    }
}
