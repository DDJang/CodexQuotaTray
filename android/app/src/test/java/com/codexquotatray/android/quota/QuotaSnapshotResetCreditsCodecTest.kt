package com.codexquotatray.android.quota

import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.protocol.ResetCreditSnapshot
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class QuotaSnapshotResetCreditsCodecTest {
    @Test
    fun roundTripPreservesAuthoritativeCountAndUnavailableDetails() {
        val original = ResetCreditSnapshot(availableCount = 2L, credits = null)

        val decoded = QuotaSnapshotResetCreditsCodec.decode(
            JSONObject().put("resetCredits", QuotaSnapshotResetCreditsCodec.encode(original)),
        )

        assertEquals(original, decoded)
        assertNull(decoded?.credits)
    }

    @Test
    fun roundTripPreservesSuccessfulEmptyDetailsAndNullableFields() {
        val original = ResetCreditSnapshot(
            availableCount = 2L,
            credits = emptyList(),
        )
        val withCredit = ResetCreditSnapshot(
            availableCount = 2L,
            credits = listOf(
                ResetCredit(
                    id = "credit-1",
                    resetType = null,
                    status = "available",
                    grantedAt = null,
                    expiresAt = 1_900_000_000L,
                    title = null,
                    description = "description",
                ),
            ),
        )

        val emptyDecoded = QuotaSnapshotResetCreditsCodec.decode(
            JSONObject().put("resetCredits", QuotaSnapshotResetCreditsCodec.encode(original)),
        )
        val creditDecoded = QuotaSnapshotResetCreditsCodec.decode(
            JSONObject().put("resetCredits", QuotaSnapshotResetCreditsCodec.encode(withCredit)),
        )

        assertEquals(emptyList<ResetCredit>(), emptyDecoded?.credits)
        assertEquals(2L, creditDecoded?.availableCount)
        assertEquals(1, creditDecoded?.credits?.size)
        assertNull(creditDecoded?.credits?.single()?.id)
        assertEquals("available", creditDecoded?.credits?.single()?.status)
        assertEquals(1_900_000_000L, creditDecoded?.credits?.single()?.expiresAt)
        assertEquals("description", creditDecoded?.credits?.single()?.description)
    }
}
