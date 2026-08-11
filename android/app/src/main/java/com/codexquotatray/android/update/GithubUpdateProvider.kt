package com.codexquotatray.android.update

import org.json.JSONArray
import org.json.JSONObject
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.util.concurrent.TimeUnit

private const val GITHUB_LATEST_RELEASE_URL =
    "https://api.github.com/repos/DDJang/CodexQuotaTray/releases/latest"

class GithubUpdateProvider(
    private val client: OkHttpClient = defaultClient(),
    private val endpoint: String = GITHUB_LATEST_RELEASE_URL,
) : UpdateProvider {
    override val source: UpdateSource = UpdateSource.GITHUB

    override fun fetchLatest(): UpdateRelease {
        val request = Request.Builder()
            .url(endpoint)
            .header("Accept", "application/vnd.github+json")
            .header("X-GitHub-Api-Version", "2022-11-28")
            .build()
        val response = try {
            client.newCall(request).execute()
        } catch (error: IOException) {
            throw UpdateProviderException("无法连接 GitHub", error)
        }
        response.use { result ->
            if (!result.isSuccessful) {
                throw UpdateProviderException("GitHub 更新服务返回 HTTP ${result.code}")
            }
            val body = result.body?.string().orEmpty()
            return parseLatestRelease(body)
        }
    }

    companion object {
        fun parseLatestRelease(raw: String): UpdateRelease {
            val root = runCatching { JSONObject(raw) }
                .getOrElse { throw UpdateProviderException("GitHub Release 数据格式无效", it) }
            if (root.optBoolean("draft", false) || root.optBoolean("prerelease", false)) {
                throw UpdateProviderException("没有可用的正式 Android Release")
            }
            val tag = root.optString("tag_name").takeIf { it.isNotBlank() }
                ?: throw UpdateProviderException("GitHub Release 缺少版本标签")
            val version = SemVer.parseReleaseTag(tag)
                ?: throw UpdateProviderException("GitHub Release 版本标签无效")
            val assets = root.optJSONArray("assets") ?: JSONArray()
            val androidTag = tag.startsWith("android-v", ignoreCase = true) ||
                tag.startsWith("v", ignoreCase = true)
            val androidAsset = if (androidTag) {
                (0 until assets.length())
                    .asSequence()
                    .mapNotNull { assets.optJSONObject(it)?.toAssetOrNull(version) }
                    .firstOrNull()
            } else {
                null
            }
            return UpdateRelease(
                tagName = tag,
                name = root.optString("name").ifBlank { tag },
                notes = root.optString("body"),
                publishedAt = root.optString("published_at").takeIf { it.isNotBlank() },
                version = version,
                androidAsset = androidAsset,
            )
        }

        private fun JSONObject.toAssetOrNull(version: SemVer): UpdateAsset? {
            val name = optString("name").takeIf { isAndroidApkName(it, version) } ?: return null
            val url = optString("browser_download_url").takeIf { it.startsWith("https://") } ?: return null
            val digest = optString("digest").takeIf { it.isNotBlank() }
            return UpdateAsset(name, url, digest)
        }

        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(8, TimeUnit.SECONDS)
            .callTimeout(10, TimeUnit.SECONDS)
            .build()
    }
}
