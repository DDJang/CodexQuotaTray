package com.codexquotatray.android.auth

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OAuthCredentialAvailabilityPolicyTest {
    @Test
    fun encryptedCredentialPresenceIsEnoughForTheFastPath() {
        assertTrue(
            OAuthCredentialAvailabilityPolicy.hasCredentials(
                hasEncryptedCredentials = true,
                migrationCompleted = true,
                hasLegacyCredentialsFile = { false },
            ),
        )
    }

    @Test
    fun legacyFileIsAvailableOnlyBeforeMigrationCompletes() {
        assertTrue(
            OAuthCredentialAvailabilityPolicy.hasCredentials(
                hasEncryptedCredentials = false,
                migrationCompleted = false,
                hasLegacyCredentialsFile = { true },
            ),
        )
        assertFalse(
            OAuthCredentialAvailabilityPolicy.hasCredentials(
                hasEncryptedCredentials = false,
                migrationCompleted = true,
                hasLegacyCredentialsFile = { true },
            ),
        )
    }

    @Test
    fun missingEncryptedAndLegacyCredentialsIsUnavailable() {
        assertFalse(
            OAuthCredentialAvailabilityPolicy.hasCredentials(
                hasEncryptedCredentials = false,
                migrationCompleted = false,
                hasLegacyCredentialsFile = { false },
            ),
        )
    }

    @Test
    fun encryptedCredentialPresenceDoesNotTouchTheLegacyFile() {
        var legacyChecks = 0

        assertTrue(
            OAuthCredentialAvailabilityPolicy.hasCredentials(
                hasEncryptedCredentials = true,
                migrationCompleted = false,
                hasLegacyCredentialsFile = {
                    legacyChecks++
                    true
                },
            ),
        )
        assertEquals(0, legacyChecks)
    }
}
