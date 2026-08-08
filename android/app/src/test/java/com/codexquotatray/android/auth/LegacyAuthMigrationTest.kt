package com.codexquotatray.android.auth

import org.junit.Assert.assertEquals
import org.junit.Test

class LegacyAuthMigrationTest {
    @Test
    fun encryptedCredentialsAreAuthoritativeEvenWhenDecryptionCouldFail() {
        assertEquals(
            LegacyAuthLoadPath.ENCRYPTED,
            LegacyAuthMigrationPolicy.choose(
                hasEncryptedCredentials = true,
                migrationCompleted = false,
            ),
        )
    }

    @Test
    fun completedMigrationBlocksLegacyReimportAfterLogout() {
        assertEquals(
            LegacyAuthLoadPath.UNAVAILABLE,
            LegacyAuthMigrationPolicy.choose(
                hasEncryptedCredentials = false,
                migrationCompleted = true,
            ),
        )
        assertEquals(
            LegacyAuthLoadPath.LEGACY,
            LegacyAuthMigrationPolicy.choose(
                hasEncryptedCredentials = false,
                migrationCompleted = false,
            ),
        )
    }
}
