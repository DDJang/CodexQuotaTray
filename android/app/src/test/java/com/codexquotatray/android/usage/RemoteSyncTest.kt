package com.codexquotatray.android.usage

import com.codexquotatray.android.ui.ResetCreditDetailState
import com.codexquotatray.android.ui.toQuotaUiModel
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.time.Instant

class RemoteSyncTest {
    @get:Rule val temporary = TemporaryFolder()
    private val readKey = "ab".repeat(32)

    @Test fun remoteTokenCacheDoesNotRestoreAnotherDeviceConnectionOrProvider() {
        val cache = TokenUsageCache.forTest(temporary.root.resolve("token-usage-cache.json"))
        val direct = snapshot(DataTransport.OPENAI, TokenUsageScope.ACCOUNT)
        val remote = snapshot(DataTransport.REMOTE, TokenUsageScope.LOCAL)
        assertTrue(cache.saveOpenAI(direct, "login"))
        assertTrue(cache.saveRemote(remote, "connection:windows"))
        assertEquals(remote, cache.loadRemote("connection:windows"))
        assertNull(cache.loadRemote("connection:ecs"))
        assertNull(cache.loadRemote("replacement:windows"))
        assertEquals(direct, cache.loadForAvailableSources(null, "login"))
    }

    @Test fun acceptsOnlyHttpsOriginsWithoutCredentialsOrPaths() {
        assertEquals(
            "https://sync.example.invalid/",
            RemoteSyncConnection.parse("https://sync.example.invalid", readKey).origin.toString(),
        )
        listOf(
            "http://sync.example.invalid",
            "https://sync.example.invalid/another",
            "https://sync.example.invalid/?key=abc",
            "https://sync.example.invalid/#fragment",
            "https://alice@sync.example.invalid",
        ).forEach { bad ->
            assertThrows(IllegalArgumentException::class.java) { RemoteSyncConnection.parse(bad, readKey) }
        }
    }

    @Test fun savedReadKeyCanOnlyBeReusedAtItsOriginalHttpsOrigin() {
        val original = RemoteSyncConnection.parse("https://sync.example.invalid:8443", readKey)
        assertSame(original, RemoteSyncConnection.parse("https://sync.example.invalid:8443/", "", original))
        assertThrows(IllegalArgumentException::class.java) {
            RemoteSyncConnection.parse("https://sync.example.invalid:10000", "", original)
        }
        val replacementKey = "cd".repeat(32)
        assertEquals(
            replacementKey,
            RemoteSyncConnection.parse("https://another.example.invalid", replacementKey, original).readKey,
        )
    }

    @Test fun aTokenOnlySnapshotPreservesTimestampAndUnknownMetrics() {
        val data = RemoteSyncJson.parse(
            """
            {
                "schemaVersion": 1,
                "quota": null,
                "tokenUsage": {
                    "schemaVersion": 1,
                    "generatedAtUtc": "2026-11-21T10:10:00+00:00",
                    "sourceTimeZone": "UTC",
                    "source": "Local",
                    "scope": "Local",
                    "summary": {"todayTokens": 25},
                    "days": []
                }
            }
            """.trimIndent(),
        )
        assertNull(data.quota)
        assertEquals(Instant.parse("2026-11-21T10:10:00Z"), data.tokenTime)
        assertEquals(25L, data.tokenUsage?.summary?.todayTokens)
        assertNull(data.tokenUsage?.summary?.lifetimeTokens)
        assertThrows(RemoteSyncException::class.java) {
            RemoteSyncJson.parse("""{"schemaVersion":1,"quota":null,"tokenUsage":null}""")
        }
    }

    @Test fun remoteQuotaShowsCreditExpiryWhileAcceptingLegacyCountOnlySnapshots() {
        val wire = """
            {
                "schemaVersion": 1,
                "quota": {
                    "schemaVersion": 1,
                    "generatedAtUtc": "2026-11-21T10:10:00Z",
                    "quotaState": "zero_windows",
                    "windows": [],
                    "resetCredits": {"availableCount": 1, "credits": DETAILS}
                }
            }
        """.trimIndent()
        val withExpiry = wire.replace("DETAILS", """[{"status":"available","expiresAt":1900000000}]""")
        val credits = RemoteSyncJson.parse(withExpiry).quota?.toQuotaUiModel()?.resetCredits
        assertEquals(1L, credits?.availableCount)
        assertEquals(ResetCreditDetailState.COMPLETE, credits?.detailState)
        assertEquals(1_900_000_000L, credits?.availableCredits?.single()?.expiresAt)

        val legacy = RemoteSyncJson.parse(wire.replace(", \"credits\": DETAILS", ""))
            .quota?.toQuotaUiModel()?.resetCredits
        assertEquals(1L, legacy?.availableCount)
        assertEquals(ResetCreditDetailState.UNAVAILABLE, legacy?.detailState)
    }
}
