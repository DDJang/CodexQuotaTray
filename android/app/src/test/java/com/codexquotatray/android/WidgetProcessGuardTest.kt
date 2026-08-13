package com.codexquotatray.android

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class WidgetProcessGuardTest {
    @Test
    fun onlyWidgetProviderSuffixIsGuarded() {
        assertFalse(isWidgetProcessName("com.codexquotatray.android"))
        assertTrue(isWidgetProcessName("com.codexquotatray.android:widgetProvider"))
        assertTrue(isWidgetProcessName("com.codexquotatray.android.debug:widgetProvider"))
        assertFalse(isWidgetProcessName(null))
    }
}
