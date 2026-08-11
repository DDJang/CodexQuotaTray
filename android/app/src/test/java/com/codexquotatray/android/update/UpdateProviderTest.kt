package com.codexquotatray.android.update

import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class UpdateProviderTest {
    private lateinit var server: MockWebServer

    @Before
    fun setUp() {
        server = MockWebServer()
        server.start()
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    @Test
    fun fetchLatestFindsAndroidOnSecondPageAfterWindowsPage() {
        server.enqueue(MockResponse().setBody(pageJson(100) {
            releaseJson(includeAndroid = false, tag = "windows-v0.1.0", version = "0.1.0")
        }))
        server.enqueue(MockResponse().setBody(pageJson(1) {
            releaseJson(includeAndroid = true, tag = "android-v0.9.0", version = "0.9.0")
        }))

        val release = provider().fetchLatest()

        assertEquals("android-v0.9.0", release.tagName)
        assertEquals(2, server.requestCount)
        assertEquals("/releases?per_page=100", server.takeRequest().path)
        assertEquals("/releases?per_page=100&page=2", server.takeRequest().path)
    }

    @Test
    fun fetchLatestDoesNotUseWindowsReleaseAfterAndroidPage() {
        server.enqueue(MockResponse().setBody(pageJson(100) {
            releaseJson(includeAndroid = true, tag = "android-v0.7.0", version = "0.7.0")
        }))
        server.enqueue(MockResponse().setBody(pageJson(1) {
            releaseJson(includeAndroid = false, tag = "windows-v0.9.0", version = "0.9.0")
        }))

        val release = provider().fetchLatest()

        assertEquals("android-v0.7.0", release.tagName)
        assertEquals(2, server.requestCount)
    }

    @Test
    fun fetchLatestChoosesHigherAndroidVersionFromLaterPage() {
        server.enqueue(MockResponse().setBody(pageJson(100) {
            releaseJson(includeAndroid = true, tag = "android-v0.7.0", version = "0.7.0")
        }))
        server.enqueue(MockResponse().setBody(pageJson(1) {
            releaseJson(includeAndroid = true, tag = "android-v0.9.0", version = "0.9.0")
        }))

        val release = provider().fetchLatest()

        assertEquals("android-v0.9.0", release.tagName)
    }

    @Test
    fun fetchLatestStopsAfterEmptySecondPage() {
        server.enqueue(MockResponse().setBody(pageJson(100) {
            releaseJson(includeAndroid = false, tag = "windows-v0.1.0", version = "0.1.0")
        }))
        server.enqueue(MockResponse().setBody("[]"))

        val error = runCatching { provider().fetchLatest() }.exceptionOrNull()

        assertTrue(error is UpdateProviderException)
        assertEquals(2, server.requestCount)
    }

    @Test
    fun fetchLatestStopsAfterShortPage() {
        server.enqueue(MockResponse().setBody(releaseJson(includeAndroid = false, tag = "windows-v0.1.0", version = "0.1.0")))

        val error = runCatching { provider().fetchLatest() }.exceptionOrNull()

        assertTrue(error is UpdateProviderException)
        assertEquals(1, server.requestCount)
    }

    @Test
    fun fetchLatestStopsAtBoundedMaximumPageCount() {
        repeat(3) {
            server.enqueue(MockResponse().setBody(pageJson(100) {
                releaseJson(includeAndroid = false, tag = "windows-v0.1.0", version = "0.1.0")
            }))
        }

        val error = runCatching { provider().fetchLatest() }.exceptionOrNull()

        assertTrue(error is UpdateProviderException)
        assertEquals(3, server.requestCount)
    }

    @Test
    fun releaseListSelectsNewestValidAndroidReleaseInsteadOfWindowsLatest() {
        val releases = JSONArray()
            .put(JSONObject(releaseJson(includeAndroid = false, tag = "windows-v0.8.0", version = "0.8.0")))
            .put(JSONObject(releaseJson(includeAndroid = true, tag = "android-v0.7.0", version = "0.7.0")))
            .put(JSONObject(releaseJson(includeAndroid = true, tag = "android-v0.6.5", version = "0.6.5")))

        val release = GithubUpdateProvider.parseLatestAndroidRelease(releases.toString())

        assertEquals("android-v0.7.0", release.tagName)
        assertEquals("CodexQuotaTray-Android-v0.7.0.apk", release.androidAsset?.name)
    }

    @Test
    fun releaseWithoutAndroidAssetFallsBackToOlderValidAndroidRelease() {
        val releases = JSONArray()
            .put(JSONObject(releaseJson(includeAndroid = false, tag = "android-v0.8.0", version = "0.8.0")))
            .put(JSONObject(releaseJson(includeAndroid = true, tag = "android-v0.7.0", version = "0.7.0")))

        val release = GithubUpdateProvider.parseLatestAndroidRelease(releases.toString())

        assertEquals("android-v0.7.0", release.tagName)
        assertNotNull(release.androidAsset)
    }

    @Test
    fun draftAndroidReleaseIsExcludedWhenStableAndroidReleaseExists() {
        val releases = JSONArray()
            .put(JSONObject(releaseJson(includeAndroid = true, tag = "android-v0.8.0", version = "0.8.0", flag = "draft" to true)))
            .put(JSONObject(releaseJson(includeAndroid = true, tag = "android-v0.7.0", version = "0.7.0")))

        val release = GithubUpdateProvider.parseLatestAndroidRelease(releases.toString())

        assertEquals("android-v0.7.0", release.tagName)
    }

    @Test
    fun noAndroidReleaseIsReportedInsteadOfUsingWindowsRelease() {
        val releases = JSONArray()
            .put(JSONObject(releaseJson(includeAndroid = true, tag = "windows-v0.8.0", version = "0.8.0")))
        val error = runCatching {
            GithubUpdateProvider.parseLatestAndroidRelease(releases.toString())
        }.exceptionOrNull()

        assertTrue(error is UpdateProviderException)
    }

    @Test
    fun latestReleaseParsesAndroidAssetAndIgnoresWindowsArtifacts() {
        val release = GithubUpdateProvider.parseLatestRelease(releaseJson(includeAndroid = true))

        assertEquals("android-v0.7.0", release.tagName)
        assertEquals(SemVer(0, 7, 0), release.version)
        assertEquals("CodexQuotaTray-Android-v0.7.0.apk", release.androidAsset?.name)
        assertEquals("sha256:${"a".repeat(64)}", release.androidAsset?.sha256Digest)
    }

    @Test
    fun missingAndroidAssetDoesNotSelectWindowsOrArchive() {
        val release = GithubUpdateProvider.parseLatestRelease(releaseJson(includeAndroid = false))
        assertNull(release.androidAsset)
    }

    @Test
    fun windowsLatestReleaseDoesNotSelectAnAndroidAsset() {
        val release = GithubUpdateProvider.parseLatestRelease(
            releaseJson(includeAndroid = true).replace("android-v0.7.0", "windows-v0.7.0"),
        )
        assertNull(release.androidAsset)
    }

    @Test
    fun semVerComparisonAndMalformedTagsAreSafe() {
        assertTrue(SemVer.parse("0.6.2")!! > SemVer.parse("0.6.1")!!)
        assertTrue(SemVer.parse("0.7.0")!! > SemVer.parse("0.6.9")!!)
        assertTrue(SemVer.parse("1.0.0")!! > SemVer.parse("0.9.9")!!)
        assertEquals(SemVer(0, 6, 1), SemVer.parseAndroidTag("android-v0.6.1"))
        assertEquals(SemVer(0, 6, 1), SemVer.parseAndroidTag("v0.6.1"))
        assertNull(SemVer.parseAndroidTag("windows-v0.6.1"))
        assertNull(SemVer.parseAndroidTag("android-vnot-a-version"))
    }

    @Test
    fun draftAndPrereleaseAreNotAcceptedAsFormalRelease() {
        listOf("draft" to true, "prerelease" to true).forEach { (key, value) ->
            val error = runCatching {
                GithubUpdateProvider.parseLatestRelease(releaseJson(includeAndroid = true, flag = key to value))
            }.exceptionOrNull()
            assertNotNull(error)
            assertTrue(error is UpdateProviderException)
        }
    }

    private fun provider(): GithubUpdateProvider = GithubUpdateProvider(
        client = OkHttpClient(),
        endpoint = server.url("/releases?per_page=100").toString(),
    )

    private fun pageJson(count: Int, releaseFactory: () -> String): String = JSONArray().apply {
        repeat(count) { put(JSONObject(releaseFactory())) }
    }.toString()

    private fun releaseJson(
        includeAndroid: Boolean,
        tag: String = "android-v0.7.0",
        version: String = "0.7.0",
        flag: Pair<String, Boolean>? = null,
    ): String {
        val assets = JSONArray()
            .put(JSONObject().put("name", "CodexQuotaTray-$version-windows.zip").put("browser_download_url", "https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v$version/a.zip"))
            .put(JSONObject().put("name", "CodexQuotaTray-$version-setup.exe").put("browser_download_url", "https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v$version/a.exe"))
        if (includeAndroid) {
            assets.put(
                JSONObject()
                    .put("name", "CodexQuotaTray-Android-v$version.apk")
                    .put("browser_download_url", "https://github.com/DDJang/CodexQuotaTray/releases/download/android-v$version/CodexQuotaTray-Android-v$version.apk")
                    .put("digest", "sha256:${"a".repeat(64)}"),
            )
        }
        return JSONObject()
            .put("tag_name", tag)
            .put("name", "Release $version")
            .put("body", "Notes")
            .put("published_at", "2026-08-11T00:00:00Z")
            .put("assets", assets)
            .apply { flag?.let { put(it.first, it.second) } }
            .toString()
    }
}
