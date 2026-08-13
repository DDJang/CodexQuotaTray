package com.codexquotatray.android

import com.codexquotatray.android.ui.QuotaUiModel
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.ui.quotaErrorUiModel
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaPageStateTest {
    @Test
    fun sameSuccessfulCacheDoesNotReplaceTheLatestFailureOnHiddenVisible() {
        val successful = loadedSnapshot(1_520_000L)
        val failure = quotaErrorUiModel("无法连接 OpenAI", successful)

        assertFalse(hasNewerQuotaSnapshot(successful, successful))
        assertTrue(failure.status == QuotaUiStatus.ERROR)
    }

    @Test
    fun newerSuccessfulCacheCanReplaceTheLatestFailure() {
        val successful = loadedSnapshot(1_520_000L)
        val newerSuccessful = loadedSnapshot(1_535_000L)

        assertTrue(hasNewerQuotaSnapshot(null, successful))
        assertTrue(hasNewerQuotaSnapshot(successful, newerSuccessful))
    }

    @Test
    fun missingOrUnversionedCacheDoesNotReplaceTheLatestFailure() {
        val successful = loadedSnapshot(1_520_000L)

        assertFalse(hasNewerQuotaSnapshot(successful, null))
        assertFalse(hasNewerQuotaSnapshot(successful, QuotaUiModel(status = QuotaUiStatus.LOADED)))
        assertFalse(hasNewerQuotaSnapshot(successful, QuotaUiModel(status = QuotaUiStatus.ERROR, updatedAtMillis = 1_535_000L)))
    }

    @Test
    fun eitherOAuthOrWindowsPairingProvidesAQuotaSource() {
        assertTrue(quotaSourceAvailable(oauthAvailable = true, windowsPairingAvailable = false))
        assertTrue(quotaSourceAvailable(oauthAvailable = false, windowsPairingAvailable = true))
        assertTrue(quotaSourceAvailable(oauthAvailable = true, windowsPairingAvailable = true))
        assertFalse(quotaSourceAvailable(oauthAvailable = false, windowsPairingAvailable = false))
    }

    @Test
    fun losingOAuthDoesNotRemoveAWindowsOnlyQuotaSource() {
        assertTrue(quotaSourceAvailable(oauthAvailable = true, windowsPairingAvailable = true))
        assertTrue(quotaSourceAvailable(oauthAvailable = false, windowsPairingAvailable = true))
        assertFalse(quotaSourceAvailable(oauthAvailable = false, windowsPairingAvailable = false))
    }

    private fun loadedSnapshot(updatedAtMillis: Long) = QuotaUiModel(
        status = QuotaUiStatus.LOADED,
        updatedAtMillis = updatedAtMillis,
    )
}
