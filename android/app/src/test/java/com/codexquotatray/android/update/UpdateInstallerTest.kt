package com.codexquotatray.android.update

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdateInstallerTest {
    @Test
    fun unknownSourcePermissionFollowsPackageManagerState() {
        assertFalse(UpdateInstaller.installPermissionGranted(false))
        assertTrue(UpdateInstaller.installPermissionGranted(true))
    }
}
