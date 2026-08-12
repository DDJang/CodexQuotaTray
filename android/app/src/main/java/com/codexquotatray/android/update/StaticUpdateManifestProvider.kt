package com.codexquotatray.android.update

import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

internal const val UPDATE_MANIFEST_URL =
    "https://raw.githubusercontent.com/DDJang/CodexQuotaTray/update-manifest/update-manifest.json"

class StaticUpdateManifestProvider(
    private val client: OkHttpClient = defaultClient(),
    private val endpoint: String = UPDATE_MANIFEST_URL,
) : UpdateProvider {
    override val source: UpdateSource = UpdateSource.GITHUB

    override fun fetchLatest(): UpdateRelease {
        val request = Request.Builder().url(endpoint).get().build()
        val response = try {
            client.newCall(request).execute()
        } catch (error: IOException) {
            throw UpdateProviderException("无法读取更新清单", error)
        }
        return response.use { result ->
            if (!result.isSuccessful) {
                throw UpdateProviderException("更新清单返回 HTTP ${result.code}")
            }
            parseAndroidRelease(result.body?.string().orEmpty())
        }
    }

    companion object {
        fun parseAndroidRelease(raw: String): UpdateRelease {
            val root = runCatching { JSONObject(raw) }
                .getOrElse { throw UpdateProviderException("更新清单格式无效", it) }
            if (root.optInt("schemaVersion", -1) != 1) {
                throw UpdateProviderException("更新清单版本不受支持")
            }
            val platform = root.optJSONObject("android")
                ?: throw UpdateProviderException("更新清单缺少 Android 信息")
            val versionText = platform.optString("version").takeIf { it.isNotBlank() }
                ?: throw UpdateProviderException("Android 更新版本缺失")
            val version = SemVer.parse(versionText)
                ?: throw UpdateProviderException("Android 更新版本无效")
            val expectedTag = "android-v$version"
            val tag = platform.optString("tag")
            if (tag != expectedTag) {
                throw UpdateProviderException("Android 更新标签与版本不匹配")
            }
            val apk = platform.optJSONObject("apk")
                ?: throw UpdateProviderException("Android 更新资产缺失")
            val expectedName = canonicalAndroidApkName(version)
            val name = apk.optString("name")
            if (name != expectedName) {
                throw UpdateProviderException("Android 更新资产名称无效")
            }
            val url = apk.optString("url").takeIf { it.startsWith("https://") }
                ?: throw UpdateProviderException("Android 更新下载地址无效")
            val sha256 = apk.optString("sha256").normalizedSha256()
                ?: throw UpdateProviderException("Android 更新摘要格式无效")
            return UpdateRelease(
                tagName = tag,
                name = tag,
                notes = platform.optString("releaseNotes"),
                publishedAt = platform.optString("publishedAt").takeIf { it.isNotBlank() },
                version = version,
                androidAsset = UpdateAsset(name, url, "sha256:$sha256"),
            )
        }

        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(8, TimeUnit.SECONDS)
            .callTimeout(10, TimeUnit.SECONDS)
            .build()
    }
}
