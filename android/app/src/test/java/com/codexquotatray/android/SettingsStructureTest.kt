package com.codexquotatray.android

import com.codexquotatray.android.source.DataSourcePriority
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class SettingsStructureTest {
    @Test
    fun sourcePrioritySelectorsKeepIndependentValueMappings() {
        assertEquals(listOf("OpenAI 优先", "Windows 优先"), sourcePriorityOptions().map { it.label })
        assertEquals(0, sourcePriorityValue(DataSourcePriority.OPENAI_FIRST))
        assertEquals(1, sourcePriorityValue(DataSourcePriority.WINDOWS_FIRST))
        assertEquals(DataSourcePriority.OPENAI_FIRST, sourcePriorityFromValue(0))
        assertEquals(DataSourcePriority.WINDOWS_FIRST, sourcePriorityFromValue(1))
    }

    @Test
    fun rootHasNoStandaloneSourceDestinationAndDataPageOwnsTheSelectorsFirst() {
        val source = settingsSource()
        assertFalse(source.contains("SOURCE(\"数据来源\")"))
        assertFalse(source.contains("SettingsNavigationRow(\"数据来源\")"))
        assertTrue(source.contains("SYNC(\"数据\")"))
        assertTrue(source.contains("SettingsSection(\"通知与数据\")"))
        assertTrue(source.contains("SettingsNavigationRow(\"数据\""))

        val dataPage = source.substringAfter("private fun ColumnScope.SyncSettings()")
            .substringBefore("private fun ColumnScope.ThemeSettings()")
        val quotaSection = dataPage.substringAfter("SettingsSection(\"额度\")")
            .substringBefore("SettingsSection(\"统计\")")
        val tokenSection = dataPage.substringAfter("SettingsSection(\"统计\")")
            .substringBefore("SettingsSection(\"电池优化\")")
        assertOrdered(quotaSection, "sourcePriorityOptions()", "SettingsDivider()", "回到前台时刷新")
        assertOrdered(tokenSection, "sourcePriorityOptions()", "SettingsDivider()", "回到前台时同步")
    }

    @Test
    fun segmentedSelectorUsesMatchingTopAndBottomInsets() {
        val source = sourceFile("SettingsUi.kt")
        val selector = source.substringAfter("internal fun SettingsSegmentedSelector(")
            .substringBefore("internal data class SettingsSegmentOption")
        assertTrue(selector.contains("top = SettingsUiTokens.segmentedBottomInset"))
        assertTrue(selector.contains("bottom = SettingsUiTokens.segmentedBottomInset"))
    }

    private fun assertOrdered(source: String, first: String, second: String, third: String) {
        val firstIndex = source.indexOf(first)
        val secondIndex = source.indexOf(second)
        val thirdIndex = source.indexOf(third)
        assertTrue(firstIndex >= 0 && firstIndex < secondIndex && secondIndex < thirdIndex)
    }

    private fun settingsSource(): String = sourceFile("SettingsActivity.kt")

    private fun sourceFile(name: String): String {
        val relative = "com/codexquotatray/android/$name"
        val candidates = listOf(
            File("android/app/src/main/java/$relative"),
            File("app/src/main/java/$relative"),
            File("src/main/java/$relative"),
        )
        return candidates.firstOrNull(File::isFile)?.readText()
            ?: error("$name source not found from ${System.getProperty("user.dir")}")
    }
}
