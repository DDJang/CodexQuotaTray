package com.codexquotatray.android.update

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdateInstallerTest {
    @Test
    fun unknownSourcePermissionIsRequiredOnlyOnAndroidOAndAbove() {
        assertTrue(UpdateInstaller.installPermissionGranted(25, false))
        assertFalse(UpdateInstaller.installPermissionGranted(26, false))
        assertTrue(UpdateInstaller.installPermissionGranted(35, true))
    }
}
