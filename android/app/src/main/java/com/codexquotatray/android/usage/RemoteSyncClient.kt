package com.codexquotatray.android.usage

import com.codexquotatray.android.protocol.DirectQuotaResult
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.io.IOException
import java.net.URI
import java.time.Instant
import java.util.concurrent.TimeUnit

enum class RemoteMachine(val path: String, val label: String) {
    WINDOWS("windows", "Windows 电脑"),
    ECS("ecs", "ECS 服务器"),
}

internal class RemoteSyncConnection private constructor(
    val origin: HttpUrl,
    val readKey: String,
) {
    companion object {
        fun parse(address: String, key: String, existing: RemoteSyncConnection? = null): RemoteSyncConnection {
            val uri = runCatching { URI(address.trim()) }.getOrNull()
            val url = uri?.takeIf {
                it.scheme.equals("https", ignoreCase = true) && it.host != null && it.rawUserInfo == null &&
                    (it.rawPath.isNullOrEmpty() || it.rawPath == "/") && it.rawQuery == null && it.rawFragment == null
            }?.toString()?.toHttpUrlOrNull()
            require(
                url != null,
            ) { "请输入 HTTPS 服务器地址，例如 https://sync.example.com。" }
            val readKey = key.trim()
            if (readKey.isEmpty() && existing != null) {
                require(url == existing.origin) { "更换服务器地址时，请重新输入读取密钥。" }
                return existing
            }
            require(readKey.matches(Regex("[0-9a-fA-F]{64}"))) { "读取密钥须为 64 位十六进制字符。" }
            return RemoteSyncConnection(url, readKey)
        }
    }
}

internal data class RemoteSyncSnapshot(
    val quota: DirectQuotaResult?,
    val tokenUsage: TokenUsageSnapshot?,
    val tokenTime: Instant?,
)

internal enum class RemoteSyncFailure { AUTHORIZATION, INVALID_RESPONSE, SERVER }

internal class RemoteSyncException(
    val userMessage: String,
    val kind: RemoteSyncFailure = RemoteSyncFailure.INVALID_RESPONSE,
) : IOException(userMessage)

internal class RemoteSyncClient(
    private val http: OkHttpClient = OkHttpClient.Builder()
        .followRedirects(false)
        .followSslRedirects(false)
        .retryOnConnectionFailure(false)
        .connectTimeout(4, TimeUnit.SECONDS)
        .callTimeout(10, TimeUnit.SECONDS)
        .build(),
) {
    fun read(connection: RemoteSyncConnection, machine: RemoteMachine): RemoteSyncSnapshot? {
        val url = connection.origin.newBuilder().addPathSegments("v1/remote").addPathSegment(machine.path).build()
        val request = Request.Builder().url(url)
            .header("Authorization", "Bearer ${connection.readKey}")
            .header("Accept", "application/json")
            .build()
        return http.newCall(request).execute().use { response ->
            when (response.code) {
                404 -> null
                401 -> throw RemoteSyncException("读取密钥无效，请重新配置远程同步。", RemoteSyncFailure.AUTHORIZATION)
                200 -> {
                    val source = response.body?.source() ?: throw RemoteSyncException("服务器未返回快照。")
                    if (source.request(MAXIMUM_BYTES + 1)) {
                        throw RemoteSyncException("服务器快照超出大小限制。")
                    }
                    RemoteSyncJson.parse(source.readUtf8())
                }
                else -> throw RemoteSyncException("远程服务器暂不可用。", RemoteSyncFailure.SERVER)
            }
        }
    }

    private companion object {
        const val MAXIMUM_BYTES = 256L * 1024
    }
}

internal object RemoteSyncJson {
    fun parse(raw: String): RemoteSyncSnapshot = try {
        val root = JSONObject(raw)
        require(root.getInt("schemaVersion") == 1)
        val quota = if (root.isNull("quota")) null else
            WindowsQuotaJson.parse(root.getJSONObject("quota").toString())
        val tokenUsage = if (root.isNull("tokenUsage")) null else
            TokenUsageJson.parse(root.getJSONObject("tokenUsage").toString())
        val tokenTime = tokenUsage?.generatedAtUtc?.let(Instant::parse)
        require(quota != null || tokenUsage != null)
        RemoteSyncSnapshot(quota, tokenUsage, tokenTime)
    } catch (_: Exception) {
        throw RemoteSyncException("服务器快照格式无效。")
    }
}
