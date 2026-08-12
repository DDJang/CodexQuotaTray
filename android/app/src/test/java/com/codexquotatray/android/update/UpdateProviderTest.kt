package com.codexquotatray.android.update

import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
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
    fun fetchLatestReadsOneStaticManifestAndUsesAndroidNode() {
        server.enqueue(MockResponse().setBody(manifestJson(androidVersion = "0.7.0", windowsVersion = "9.9.9")))

        val release = provider().fetchLatest()

        assertEquals("android-v0.7.0", release.tagName)
        assertEquals(SemVer(0, 7, 0), release.version)
        assertEquals("CodexQuotaTray-Android-v0.7.0.apk", release.androidAsset?.name)
        assertEquals("sha256:${"a".repeat(64)}", release.androidAsset?.sha256Digest)
        assertEquals(1, server.requestCount)
        assertEquals("/update-manifest.json", server.takeRequest().path)
        assertTrue(!UPDATE_MANIFEST_URL.contains("api.github.com"))
    }

    @Test
    fun parserRejectsMissingPlatformMalformedManifestAndInvalidDigest() {
        listOf(
            "{}",
            "not-json",
            manifestJson().replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            manifestJson().replace("${"a".repeat(64)}", "bad-hash"),
            manifestJson().replace("android-v0.7.0", "windows-v0.7.0"),
        ).forEach { raw ->
            val error = runCatching { StaticUpdateManifestProvider.parseAndroidRelease(raw) }.exceptionOrNull()
            assertTrue(error is UpdateProviderException)
        }
    }

    @Test
    fun fetchLatestReportsHttpAndNetworkFailures() {
        server.enqueue(MockResponse().setResponseCode(503))
        assertTrue(runCatching { provider().fetchLatest() }.exceptionOrNull() is UpdateProviderException)

        val unavailable = StaticUpdateManifestProvider(
            client = OkHttpClient(),
            endpoint = "http://127.0.0.1:1/update-manifest.json",
        )
        assertTrue(runCatching { unavailable.fetchLatest() }.exceptionOrNull() is UpdateProviderException)
    }

    @Test
    fun semVerComparisonAndMalformedTagsRemainStrict() {
        assertTrue(SemVer.parse("0.7.0")!! > SemVer.parse("0.6.9")!!)
        assertEquals(SemVer(0, 6, 1), SemVer.parseAndroidTag("android-v0.6.1"))
        assertNull(SemVer.parseAndroidTag("windows-v0.6.1"))
        assertNull(SemVer.parseAndroidTag("android-vnot-a-version"))
    }

    private fun provider() = StaticUpdateManifestProvider(
        client = OkHttpClient(),
        endpoint = server.url("/update-manifest.json").toString(),
    )

    private fun manifestJson(
        androidVersion: String = "0.7.0",
        windowsVersion: String = "0.8.0",
    ): String = JSONObject()
        .put("schemaVersion", 1)
        .put(
            "windows",
            JSONObject()
                .put("version", windowsVersion)
                .put("tag", "windows-v$windowsVersion")
                .put(
                    "installer",
                    JSONObject()
                        .put("name", "CodexQuotaTray-$windowsVersion-setup.exe")
                        .put("url", "https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v$windowsVersion/CodexQuotaTray-$windowsVersion-setup.exe")
                        .put("sha256", "b".repeat(64)),
                ),
        )
        .put(
            "android",
            JSONObject()
                .put("version", androidVersion)
                .put("tag", "android-v$androidVersion")
                .put("releaseNotes", "Notes")
                .put("publishedAt", "2026-08-12T00:00:00Z")
                .put(
                    "apk",
                    JSONObject()
                        .put("name", "CodexQuotaTray-Android-v$androidVersion.apk")
                        .put("url", "https://github.com/DDJang/CodexQuotaTray/releases/download/android-v$androidVersion/CodexQuotaTray-Android-v$androidVersion.apk")
                        .put("sha256", "a".repeat(64)),
                ),
        )
        .toString()
}
