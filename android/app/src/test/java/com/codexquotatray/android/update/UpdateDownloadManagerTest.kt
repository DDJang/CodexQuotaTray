package com.codexquotatray.android.update

import java.io.ByteArrayInputStream
import java.io.File
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
}
