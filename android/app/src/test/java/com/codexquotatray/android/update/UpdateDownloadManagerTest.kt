package com.codexquotatray.android.update

import java.io.ByteArrayInputStream
import java.io.File
import java.io.InputStream
import java.nio.file.Files
import java.security.MessageDigest
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdateDownloadManagerTest {
    @Test
    fun progressFormattingCoversKnownUnknownAndSpeedPrecision() {
        val known = UpdateDownloadProgress(
            UpdateDownloadPhase.DOWNLOADING,
            bytesDownloaded = (35.8 * 1024 * 1024).toLong(),
            totalBytes = (59.6 * 1024 * 1024).toLong(),
            bytesPerSecond = 2.1 * 1024 * 1024,
        )
        assertTrue(UpdateDownloadFormatting.formatProgress(known).contains("35.8 MB / 59.6 MB"))
        assertTrue(UpdateDownloadFormatting.formatProgress(known).endsWith("2.1 MB/s"))
        assertTrue(UpdateDownloadFormatting.formatProgress(known.copy(totalBytes = null)).startsWith("35.8 MB"))
        assertTrue(UpdateDownloadFormatting.formatSpeed(0.42 * 1024 * 1024).startsWith("0.42 MB/s"))
    }
    @Test
    fun matchingDigestIsAccepted() {
        val directory = Files.createTempDirectory("codex-update").toFile()
        val bytes = "apk-bytes".toByteArray()
        val digest = sha256(bytes)
        val manager = manager(directory, bytes)
        val result = awaitDownload(manager, UpdateAsset(canonicalAndroidApkName(SemVer(0, 7, 0)), "https://github.com/DDJang/CodexQuotaTray/a.apk", "sha256:$digest"))
        assertTrue(result.isSuccess)
        assertTrue(result.getOrThrow().isFile)
        manager.close()
        directory.deleteRecursively()
    }

    @Test
    fun mismatchedDigestAndDownloadFailureCleanUpTemporaryFiles() {
        val directory = Files.createTempDirectory("codex-update").toFile()
        val manager = manager(directory, "apk-bytes".toByteArray())
        val result = awaitDownload(manager, UpdateAsset(canonicalAndroidApkName(SemVer(0, 7, 0)), "https://github.com/DDJang/CodexQuotaTray/a.apk", "sha256:${"0".repeat(64)}"))
        assertTrue(result.isFailure)
        assertTrue(directory.listFiles().orEmpty().isEmpty())
        manager.close()
        directory.deleteRecursively()
    }

    @Test
    fun transportFailureCleansUpPartialDownload() {
        val directory = Files.createTempDirectory("codex-update").toFile()
        val manager = UpdateDownloadManager(
            directory,
            UpdateDownloadTransport { throw UpdateDownloadException("network down") },
            Executors.newSingleThreadExecutor(),
        )
        val result = awaitDownload(manager, UpdateAsset(canonicalAndroidApkName(SemVer(0, 7, 0)), "https://github.com/DDJang/CodexQuotaTray/a.apk"))
        assertTrue(result.isFailure)
        assertTrue(directory.listFiles().orEmpty().isEmpty())
        manager.close()
        directory.deleteRecursively()
    }

    @Test
    fun nonApkAssetIsRejectedBeforeTransport() {
        val directory = Files.createTempDirectory("codex-update").toFile()
        var opened = false
        val manager = UpdateDownloadManager(
            directory,
            UpdateDownloadTransport {
                opened = true
                UpdateDownloadResponse(it, 0, ByteArrayInputStream(ByteArray(0)))
            },
            Executors.newSingleThreadExecutor(),
        )
        val result = awaitDownload(manager, UpdateAsset("CodexQuotaTray-Android-v0.7.0.zip", "https://github.com/DDJang/CodexQuotaTray/a.zip"))
        assertTrue(result.isFailure)
        assertFalse(opened)
        manager.close()
        directory.deleteRecursively()
    }

    @Test
    fun downloaderAcceptsOnlyHttpsGithubRedirectHosts() {
        OkHttpUpdateDownloadTransport.validateGithubUrl("https://github.com/DDJang/CodexQuotaTray/a.apk")
        OkHttpUpdateDownloadTransport.validateGithubUrl("https://release-assets.githubusercontent.com/a.apk")
        assertFails { OkHttpUpdateDownloadTransport.validateGithubUrl("http://github.com/a.apk") }
        assertFails { OkHttpUpdateDownloadTransport.validateGithubUrl("https://example.com/a.apk") }
    }

    @Test
    fun browserFallbackUsesTheSameTrustedUrlRules() {
        UpdateDownloadUrlSecurity.validateGithubUrl("https://github.com/DDJang/CodexQuotaTray/a.apk")
        UpdateDownloadUrlSecurity.validateGithubUrl("https://githubassets.com/a.apk")
        assertFails { UpdateDownloadUrlSecurity.validateGithubUrl("http://github.com/a.apk") }
        assertFails { UpdateDownloadUrlSecurity.validateGithubUrl("https://example.com/a.apk") }
    }

    @Test
    fun cancellationDeletesPartialFilesAndAllowsNextDownload() {
        val directory = Files.createTempDirectory("codex-update").toFile()
        val transport = SlowCancellableTransport()
        val manager = UpdateDownloadManager(directory, transport, Executors.newSingleThreadExecutor())
        val asset = UpdateAsset(canonicalAndroidApkName(SemVer(0, 7, 0)), "https://github.com/DDJang/CodexQuotaTray/a.apk")
        val cancelled = CountDownLatch(1)
        var cancelledResult: Result<File>? = null
        manager.download(asset, callback = {
            cancelledResult = it
            cancelled.countDown()
        })
        assertTrue(transport.opened.await(2, TimeUnit.SECONDS))
        assertTrue(manager.cancel())
        assertTrue(cancelled.await(2, TimeUnit.SECONDS))
        assertTrue(cancelledResult?.exceptionOrNull() is UpdateDownloadCancelledException)
        assertTrue(directory.listFiles().orEmpty().isEmpty())

        val completed = awaitDownload(manager, asset)
        assertTrue(completed.isSuccess)
        manager.close()
        directory.deleteRecursively()
    }

    private fun manager(directory: File, bytes: ByteArray) = UpdateDownloadManager(
        directory,
        UpdateDownloadTransport { url -> UpdateDownloadResponse(url, bytes.size.toLong(), ByteArrayInputStream(bytes)) },
        Executors.newSingleThreadExecutor(),
    )

    private fun awaitDownload(manager: UpdateDownloadManager, asset: UpdateAsset): Result<File> {
        val latch = CountDownLatch(1)
        var result: Result<File>? = null
        manager.download(asset) {
            result = it
            latch.countDown()
        }
        assertTrue(latch.await(2, TimeUnit.SECONDS))
        return result ?: error("download callback missing")
    }

    private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
        .digest(bytes)
        .joinToString("") { byte -> "%02x".format(byte.toInt() and 0xff) }

    private fun assertFails(block: () -> Unit) {
        assertTrue(runCatching(block).isFailure)
    }

    private class SlowCancellableTransport : CancellableUpdateDownloadTransport {
        val opened = CountDownLatch(1)
        private var openCount = 0
        @Volatile private var cancelled = false

        override fun open(url: String): UpdateDownloadResponse {
            openCount++
            cancelled = false
            opened.countDown()
            return if (openCount == 1) {
                UpdateDownloadResponse(url, 1024 * 1024, SlowInputStream())
            } else {
                UpdateDownloadResponse(url, 4, ByteArrayInputStream(byteArrayOf(1, 2, 3, 4)))
            }
        }

        override fun cancel() {
            cancelled = true
        }

        override fun clear() {
        }

        private inner class SlowInputStream : InputStream() {
            private var remaining = 1024 * 1024

            override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
                Thread.sleep(5)
                if (cancelled) return -1
                if (remaining == 0) return -1
                val count = minOf(1024, remaining, length)
                java.util.Arrays.fill(buffer, offset, offset + count, 1)
                remaining -= count
                return count
            }

            override fun read(): Int = if (remaining-- > 0) 1 else -1
        }
    }
}
