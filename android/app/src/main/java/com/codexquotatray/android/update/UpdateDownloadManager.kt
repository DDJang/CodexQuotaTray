package com.codexquotatray.android.update

import android.content.Context
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.TimeUnit

data class UpdateDownloadResponse(
    val finalUrl: String,
    val contentLength: Long,
    val body: InputStream,
)

fun interface UpdateDownloadTransport {
    fun open(url: String): UpdateDownloadResponse
}

interface CancellableUpdateDownloadTransport : UpdateDownloadTransport {
    fun cancel()

    fun clear()
}

open class UpdateDownloadException(message: String, cause: Throwable? = null) : IOException(message, cause)

class UpdateDownloadCancelledException : UpdateDownloadException("下载已取消")

class OkHttpUpdateDownloadTransport(
    private val client: OkHttpClient = defaultClient(),
) : CancellableUpdateDownloadTransport {
    @Volatile
    private var activeCall: okhttp3.Call? = null

    override fun open(url: String): UpdateDownloadResponse {
        validateGithubUrl(url)
        val call = client.newCall(Request.Builder().url(url).get().build())
        activeCall = call
        val response = try {
            call.execute()
        } catch (error: IOException) {
            throw UpdateDownloadException("下载更新失败", error)
        }
        if (!response.isSuccessful) {
            response.close()
            throw UpdateDownloadException("下载更新失败：HTTP ${response.code}")
        }
        val finalUrl = response.request.url.toString()
        try {
            validateGithubUrl(finalUrl)
        } catch (error: UpdateDownloadException) {
            response.close()
            throw error
        }
        val body = response.body ?: run {
            response.close()
            throw UpdateDownloadException("更新安装包为空")
        }
        return UpdateDownloadResponse(finalUrl, body.contentLength(), body.byteStream())
    }

    override fun cancel() {
        activeCall?.cancel()
    }

    override fun clear() {
        activeCall = null
    }

    companion object {
        internal fun validateGithubUrl(raw: String) {
            UpdateDownloadUrlSecurity.validateGithubUrl(raw)
        }

        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .callTimeout(2, TimeUnit.MINUTES)
            .followRedirects(true)
            .followSslRedirects(true)
            .build()
    }
}

class UpdateDownloadManager(
    private val cacheDirectory: File,
    private val transport: UpdateDownloadTransport,
    private val executor: ExecutorService = Executors.newSingleThreadExecutor(),
) : AutoCloseable {
    private val busy = AtomicBoolean(false)
    private val cancelRequested = AtomicBoolean(false)

    constructor(context: Context) : this(
        cacheDirectory = File(context.applicationContext.cacheDir, "codex-update"),
        transport = OkHttpUpdateDownloadTransport(),
        executor = Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "codex-update-download").apply { isDaemon = true }
        },
    )

    fun download(
        asset: UpdateAsset,
        onProgress: (UpdateDownloadProgress) -> Unit = { _ -> },
        callback: (Result<File>) -> Unit,
    ) {
        if (!busy.compareAndSet(false, true)) {
            callback(Result.failure(UpdateDownloadException("更新正在下载中")))
            return
        }
        cancelRequested.set(false)
        executor.execute {
            val result = try {
                Result.success(downloadBlocking(asset, onProgress))
            } catch (error: Throwable) {
                Result.failure(error)
            }
            busy.set(false)
            (transport as? CancellableUpdateDownloadTransport)?.clear()
            callback(result)
        }
    }

    fun cancel(): Boolean {
        if (!busy.get()) return false
        cancelRequested.set(true)
        (transport as? CancellableUpdateDownloadTransport)?.cancel()
        return true
    }

    fun cleanupStaleFiles(nowMillis: Long = System.currentTimeMillis(), maxAgeMillis: Long = MAX_CACHE_AGE_MILLIS) {
        cacheDirectory.listFiles()?.forEach { file ->
            if (nowMillis - file.lastModified() > maxAgeMillis) file.delete()
        }
    }

    private fun downloadBlocking(asset: UpdateAsset, onProgress: (UpdateDownloadProgress) -> Unit): File {
        val versionText = asset.name.removePrefix("CodexQuotaTray-Android-v").removeSuffix(".apk")
        val assetVersion = SemVer.parse(versionText)
        if (!asset.name.endsWith(".apk", ignoreCase = true) ||
            assetVersion == null || asset.name != canonicalAndroidApkName(assetVersion)) {
            throw UpdateDownloadException("更新资产不是受支持的 Android APK")
        }
        cacheDirectory.mkdirs()
        val target = File(cacheDirectory, "pending-${asset.name}")
        val temporary = File(cacheDirectory, "${asset.name}.part")
        target.delete()
        temporary.delete()
        val expectedDigest = asset.sha256Digest?.let { digest ->
            digest.normalizedSha256() ?: throw UpdateDownloadException("更新摘要格式不受支持")
        }
        var lastProgress = UpdateDownloadProgress.Idle
        fun report(progress: UpdateDownloadProgress, force: Boolean = false) {
            lastProgress = progress
            onProgress(progress)
        }

        try {
            checkCancelled()
            val response = transport.open(asset.browserDownloadUrl)
            response.body.use { input ->
                val total = response.contentLength.takeIf { it > 0L }
                var downloaded = 0L
                val digest = MessageDigest.getInstance("SHA-256")
                var lastReportNanos = 0L
                var lastSampleNanos = 0L
                var lastSampleBytes = 0L
                fun reportDownload(force: Boolean = false) {
                    val now = System.nanoTime()
                    if (!force && lastReportNanos != 0L && now - lastReportNanos < PROGRESS_INTERVAL_NANOS) return
                    val sampleNanos = now - lastSampleNanos
                    val speed = if (sampleNanos > 0L && downloaded > lastSampleBytes) {
                        (downloaded - lastSampleBytes) * 1_000_000_000.0 / sampleNanos
                    } else {
                        null
                    }
                    report(UpdateDownloadProgress(UpdateDownloadPhase.DOWNLOADING, downloaded, total, speed), force)
                    lastReportNanos = now
                    lastSampleNanos = now
                    lastSampleBytes = downloaded
                }
                reportDownload(force = true)
                temporary.outputStream().use { output ->
                    val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                    while (true) {
                        checkCancelled()
                        val read = input.read(buffer)
                        if (read < 0) break
                        if (read == 0) continue
                        downloaded += read
                        if (downloaded > MAX_APK_BYTES) throw UpdateDownloadException("更新安装包过大")
                        output.write(buffer, 0, read)
                        digest.update(buffer, 0, read)
                        reportDownload()
                    }
                    output.flush()
                }
                reportDownload(force = true)
                report(
                    UpdateDownloadProgress(
                        UpdateDownloadPhase.VERIFYING,
                        downloaded,
                        total ?: downloaded,
                    ),
                    force = true,
                )
                if (expectedDigest != null) {
                    val actual = digest.digest().joinToString("") { byte -> "%02x".format(byte.toInt() and 0xff) }
                    if (actual != expectedDigest) throw UpdateDownloadException("更新安装包校验失败")
                }
            }
            checkCancelled()
            if (!temporary.renameTo(target)) throw UpdateDownloadException("无法保存更新安装包")
            return target
        } catch (error: Throwable) {
            temporary.delete()
            target.delete()
            val normalizedError = if (cancelRequested.get()) {
                UpdateDownloadCancelledException()
            } else {
                error
            }
            val finalProgress = lastProgress.copy(
                phase = if (normalizedError is UpdateDownloadCancelledException) UpdateDownloadPhase.CANCELLED else UpdateDownloadPhase.FAILED,
                bytesPerSecond = null,
            )
            report(finalProgress, force = true)
            if (normalizedError is UpdateDownloadException) throw normalizedError
            throw UpdateDownloadException("下载更新失败", normalizedError)
        }
    }

    private fun checkCancelled() {
        if (cancelRequested.get()) throw UpdateDownloadCancelledException()
    }

    override fun close() {
        executor.shutdownNow()
    }

    companion object {
        private const val PROGRESS_INTERVAL_NANOS = 300_000_000L
        private const val MAX_APK_BYTES = 100L * 1024L * 1024L
        private const val MAX_CACHE_AGE_MILLIS = 7L * 24L * 60L * 60L * 1000L
    }
}
