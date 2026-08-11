package com.codexquotatray.android.update

import org.json.JSONArray
import org.json.JSONObject
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.util.concurrent.TimeUnit

private const val GITHUB_RELEASES_URL =
    "https://api.github.com/repos/DDJang/CodexQuotaTray/releases?per_page=100"

class GithubUpdateProvider(
    private val client: OkHttpClient = defaultClient(),
    private val endpoint: String = GITHUB_RELEASES_URL,
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
            return parseLatestAndroidRelease(body)
        }
    }

    companion object {
        /** Parses the release list and chooses the newest valid Android release. */
        fun parseLatestAndroidRelease(raw: String): UpdateRelease {
            val releases = parseReleaseList(raw)
            val androidReleases = releases.filter { it.tagName.isAndroidReleaseTag() }
            val validAndroidReleases = androidReleases.filter { it.androidAsset != null }
            return latest(validAndroidReleases)
                ?: latest(androidReleases)
                ?: throw UpdateProviderException("当前没有可用的 Android Release")
        }

        /** Parses formal, semantically versioned releases from GitHub's array response. */
        fun parseReleaseList(raw: String): List<UpdateRelease> {
            val root = runCatching { JSONArray(raw) }
                .getOrElse { throw UpdateProviderException("GitHub Release 列表格式无效", it) }
            return (0 until root.length())
                .mapNotNull { root.optJSONObject(it)?.toReleaseOrNull() }
        }

        /** Kept as a parser helper for tests and callers that already have one release object. */
        fun parseLatestRelease(raw: String): UpdateRelease {
            val root = runCatching { JSONObject(raw) }
                .getOrElse { throw UpdateProviderException("GitHub Release 数据格式无效", it) }
            return root.toReleaseOrNull()
                ?: throw UpdateProviderException("GitHub Release 不是有效的正式版本")
        }

        private fun latest(releases: List<UpdateRelease>): UpdateRelease? =
            releases.maxWithOrNull(
                compareBy<UpdateRelease> { it.version }
                    .thenBy { it.publishedAt.orEmpty() },
            )

        private fun JSONObject.toReleaseOrNull(): UpdateRelease? {
            if (optBoolean("draft", false) || optBoolean("prerelease", false)) return null
            val tag = optString("tag_name").takeIf { it.isNotBlank() } ?: return null
            val version = SemVer.parseReleaseTag(tag) ?: return null
            val assets = optJSONArray("assets") ?: JSONArray()
            val androidAsset = if (tag.isAndroidReleaseTag()) {
                (0 until assets.length())
                    .asSequence()
                    .mapNotNull { assets.optJSONObject(it)?.toAssetOrNull(version) }
                    .firstOrNull()
            } else {
                null
            }
            return UpdateRelease(
                tagName = tag,
                name = optString("name").ifBlank { tag },
                notes = optString("body"),
                publishedAt = optString("published_at").takeIf { it.isNotBlank() },
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

        private fun String.isAndroidReleaseTag(): Boolean =
            startsWith("android-v", ignoreCase = true)

        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(8, TimeUnit.SECONDS)
            .callTimeout(10, TimeUnit.SECONDS)
            .build()
    }
}
