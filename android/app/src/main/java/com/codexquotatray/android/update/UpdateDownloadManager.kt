package com.codexquotatray.android.update

import android.content.Context
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.HttpUrl.Companion.toHttpUrl
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

class UpdateDownloadException(message: String, cause: Throwable? = null) : IOException(message, cause)

class OkHttpUpdateDownloadTransport(
    private val client: OkHttpClient = defaultClient(),
) : UpdateDownloadTransport {
    override fun open(url: String): UpdateDownloadResponse {
        validateGithubUrl(url)
        val response = try {
            client.newCall(Request.Builder().url(url).get().build()).execute()
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

    companion object {
        internal fun validateGithubUrl(raw: String) {
            val url = runCatching { raw.toHttpUrl() }
                .getOrElse { throw UpdateDownloadException("更新下载地址无效") }
            val host = url.host.lowercase()
            val allowedHost = host == "github.com" ||
                host.endsWith(".github.com") ||
                host == "githubusercontent.com" ||
                host.endsWith(".githubusercontent.com") ||
                host == "githubassets.com" ||
                host.endsWith(".githubassets.com")
            if (!url.isHttps || !allowedHost) {
                throw UpdateDownloadException("更新下载地址不受信任")
            }
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

    constructor(context: Context) : this(
        cacheDirectory = File(context.applicationContext.cacheDir, "codex-update"),
        transport = OkHttpUpdateDownloadTransport(),
        executor = Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "codex-update-download").apply { isDaemon = true }
        },
    )

    fun download(
        asset: UpdateAsset,
        onProgress: (Long, Long) -> Unit = { _, _ -> },
        callback: (Result<File>) -> Unit,
    ) {
        if (!busy.compareAndSet(false, true)) {
            callback(Result.failure(UpdateDownloadException("更新正在下载中")))
            return
        }
        executor.execute {
            val result = runCatching { downloadBlocking(asset, onProgress) }
            busy.set(false)
            callback(result)
        }
    }

    fun cleanupStaleFiles(nowMillis: Long = System.currentTimeMillis(), maxAgeMillis: Long = MAX_CACHE_AGE_MILLIS) {
        cacheDirectory.listFiles()?.forEach { file ->
            if (nowMillis - file.lastModified() > maxAgeMillis) file.delete()
        }
    }

    private fun downloadBlocking(asset: UpdateAsset, onProgress: (Long, Long) -> Unit): File {
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
        try {
            val response = transport.open(asset.browserDownloadUrl)
            response.body.use { input ->
                val total = response.contentLength.coerceAtLeast(0L)
                var downloaded = 0L
                val digest = MessageDigest.getInstance("SHA-256")
                temporary.outputStream().use { output ->
                    val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        if (read == 0) continue
                        downloaded += read
                        if (downloaded > MAX_APK_BYTES) throw UpdateDownloadException("更新安装包过大")
                        output.write(buffer, 0, read)
                        digest.update(buffer, 0, read)
                        onProgress(downloaded, total)
                    }
                }
                if (expectedDigest != null) {
                    val actual = digest.digest().joinToString("") { byte -> "%02x".format(byte.toInt() and 0xff) }
                    if (actual != expectedDigest) throw UpdateDownloadException("更新安装包校验失败")
                }
            }
            if (!temporary.renameTo(target)) throw UpdateDownloadException("无法保存更新安装包")
            return target
        } catch (error: Throwable) {
            temporary.delete()
            target.delete()
            if (error is UpdateDownloadException) throw error
            throw UpdateDownloadException("下载更新失败", error)
        }
    }

    override fun close() {
        executor.shutdownNow()
    }

    companion object {
        private const val MAX_APK_BYTES = 100L * 1024L * 1024L
        private const val MAX_CACHE_AGE_MILLIS = 7L * 24L * 60L * 60L * 1000L
    }
}
