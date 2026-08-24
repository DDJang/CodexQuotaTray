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

        val dataPage = source.substringAfter("private fun ColumnScope.SyncSettings(")
            .substringBefore("private fun ColumnScope.ThemeSettings()")
        val quotaSection = dataPage.substringAfter("SettingsSection(\"额度\")")
            .substringBefore("SettingsSection(\"统计\")")
        val tokenSection = dataPage.substringAfter("SettingsSection(\"统计\")")
            .substringBefore("SettingsSection(\"电池优化\")")
        assertOrdered(
            quotaSection,
            "SettingsInlineLabel(\"数据来源\")",
            "sourcePriorityOptions()",
            "SettingsDivider()",
            "回到前台时刷新",
        )
        assertOrdered(
            tokenSection,
            "SettingsInlineLabel(\"数据来源\")",
            "sourcePriorityOptions()",
            "SettingsDivider()",
            "回到前台时同步",
        )
    }

    @Test
    fun segmentedSelectorUsesMatchingTopAndBottomInsets() {
        val source = sourceFile("SettingsUi.kt")
        val selector = source.substringAfter("internal fun SettingsSegmentedSelector(")
            .substringBefore("internal data class SettingsSegmentOption")
        assertTrue(selector.contains("top = SettingsUiTokens.segmentedBottomInset"))
        assertTrue(selector.contains("bottom = SettingsUiTokens.segmentedBottomInset"))
    }

    @Test
    fun quotaRingFixtureEntryIsDebugOnly() {
        val source = settingsSource()
        assertTrue(source.contains("if (BuildConfig.DEBUG)"))
        assertTrue(source.contains("SettingsSection(\"开发者选项\")"))
        assertTrue(source.contains("title = \"Quota Ring Fixture\""))
        assertTrue(source.contains("DEBUG_QUOTA_RING_FIXTURE_ACTIVITY"))
    }

    @Test
    fun liquidToggleSettingsPagesUseLocalCardBackdropsAndStableStateAdapters() {
        val source = settingsSource()
        val scaffold = source.substringAfter("val backgroundColor = palette.color(palette.background)")
            .substringBefore("if (showClearPairingDialog)")

        assertTrue(scaffold.contains(".layerBackdrop(pageBackdrop)"))
        assertTrue(scaffold.contains(".background(backgroundColor)"))
        val pageSource = scaffold.substringAfter(".layerBackdrop(pageBackdrop)")
            .substringBefore("SettingsGradientBlurHeader(")
        assertTrue(pageSource.contains("SettingsContent("))
        assertTrue(scaffold.contains("backdrop = pageBackdrop"))
        assertFalse(pageSource.contains(".background(backgroundColor)"))

        val notificationPage = source.substringAfter("private fun ColumnScope.NotificationSettings(")
            .substringBefore("private fun ColumnScope.SyncSettings(")
        val syncPage = source.substringAfter("private fun ColumnScope.SyncSettings(")
            .substringBefore("private fun ColumnScope.ThemeSettings(")
        assertTrue(notificationPage.contains("SettingsToggleRow"))
        assertTrue(syncPage.contains("SettingsToggleRow"))

        val toggleRow = sourceFile("SettingsUi.kt")
            .substringAfter("internal fun SettingsToggleRow(")
            .substringBefore("internal fun SettingsSelectionRow(")
        assertTrue(toggleRow.contains("val toggleBackdrop = rememberLayerBackdrop()"))
        assertTrue(toggleRow.contains(".fillMaxSize()"))
        assertTrue(toggleRow.contains(".layerBackdrop(toggleBackdrop)"))
        assertTrue(toggleRow.contains(".background(palette.color(palette.surface))"))
        assertTrue(toggleRow.contains("val selectedProvider = remember { { checkedState.value } }"))
        assertTrue(toggleRow.contains("val selectionSink = remember { { value: Boolean -> onChangeState.value(value) } }"))
        assertTrue(toggleRow.contains("accentColor = palette.color(palette.accent)"))
    }

    @Test
    fun liquidMainDockUsesStableAdaptersForExternalSelectionState() {
        val source = sourceFile("GlassComponents.kt")
        val dock = source.substringAfter("internal fun LiquidMainDock(")
            .substringBefore("private fun LiquidTabCapsule(")

        assertTrue(dock.contains("rememberUpdatedState(selectedIndex)"))
        assertTrue(dock.contains("rememberUpdatedState(onSelected)"))
        assertTrue(dock.contains("var requestedIndex by remember { mutableIntStateOf(selectedIndex) }"))
        assertTrue(dock.contains("LaunchedEffect(selectedIndex)"))
        assertTrue(dock.contains("requestedIndex = selectedIndex"))
        assertTrue(dock.contains("val requestedIndexState = rememberUpdatedState(requestedIndex)"))
        assertTrue(dock.contains("val selectedIndexProvider = remember { { requestedIndexState.value } }"))
        assertTrue(dock.contains("val selectionSink = remember { { index: Int -> onSelectedState.value(index) } }"))
        assertTrue(dock.contains("val hapticFeedback = LocalHapticFeedback.current"))
        assertTrue(dock.contains("selectedTabIndex = selectedIndexProvider"))
        assertTrue(dock.contains("onTabSelected = { index ->"))
        assertTrue(dock.contains("if (selectedIndexState.value != index)"))
        assertTrue(dock.contains("hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)"))
        assertTrue(dock.contains("selectionSink(index)"))
        assertTrue(dock.contains("LiquidBottomTab(onClick = { requestedIndex = 0 })"))
        assertTrue(dock.contains("LiquidBottomTab(onClick = { requestedIndex = 1 })"))
        assertFalse(dock.contains("selectedTabIndex = { selectedIndex }"))
        assertFalse(dock.contains("onTabSelected = selectionSink"))
        assertFalse(dock.contains("LiquidBottomTab(onClick = { selectionSink(0) })"))
        assertFalse(dock.contains("LiquidBottomTab(onClick = { selectionSink(1) })"))
    }

    @Test
    fun mainPageKeepsAnimatedContentOutsideTheStaticChromeBackdrop() {
        val source = sourceFile("MainActivity.kt")
        val chrome = source.substringAfter("val chromeBackdrop = rememberLayerBackdrop()")
            .substringBefore("Box(Modifier.align(Alignment.TopEnd)")
        val staticSource = chrome.substringBefore("Column(")
        val dynamicPage = chrome.substringAfter("Column(")

        assertTrue(staticSource.contains(".layerBackdrop(chromeBackdrop)"))
        assertTrue(staticSource.contains(".background(palette.color(palette.background))"))
        assertFalse(staticSource.contains("AnimatedContent("))
        assertTrue(dynamicPage.contains("AnimatedContent("))
        assertTrue(dynamicPage.contains("fadeIn(animationSpec = tween(200))"))
        assertTrue(dynamicPage.contains("slideInHorizontally("))
        assertTrue(dynamicPage.contains("fadeOut(animationSpec = tween(160))"))
        assertTrue(dynamicPage.contains("slideOutHorizontally("))
        assertTrue(source.contains("backdrop = chromeBackdrop"))
    }

    @Test
    fun liquidBottomTabsUseKyantTabsBackdropWithUpstreamHiddenTabScaling() {
        val source = sourceFile("liquidglass/LiquidBottomTabs.kt")

        assertTrue(source.contains("val tabsBackdrop = rememberLayerBackdrop()"))
        assertTrue(source.contains("CompositionLocalProvider"))
        assertTrue(source.contains("lerp(1f, 1.2f, dampedDragAnimation.pressProgress)"))
        assertFalse(source.contains("LocalLiquidBottomTabScale provides { 1f }"))
        assertTrue(source.contains(".alpha(0f)"))
        assertTrue(source.contains(".layerBackdrop(tabsBackdrop)"))
        assertTrue(source.contains("ColorFilter.tint(accentColor)"))
        assertTrue(source.contains("backdrop = rememberCombinedBackdrop("))
        assertTrue(source.contains("tabsBackdrop"))
        assertTrue(source.contains("backdrop = backdrop"))
        assertTrue(source.contains("chromaticAberration = true"))
        assertTrue(source.contains("pressedScale = 78f / 56f"))
    }

    @Test
    fun liquidToggleUsesTheProjectAccentAndTheWholeTrackAsItsInteractionTarget() {
        val source = sourceFile("liquidglass/LiquidToggle.kt")

        assertTrue(source.contains("accentColor: Color"))
        assertFalse(source.contains("0xFF34C759"))
        assertFalse(source.contains("0xFF30D158"))
        assertTrue(
            source.contains(
                ".size(64f.dp, 28f.dp)\n" +
                    "                .semantics { role = Role.Switch }\n" +
                    "                .then(if (enabled) dampedDragAnimation.modifier else Modifier)",
            ),
        )
    }

    @Test
    fun liquidBottomTabsFixtureIsDebugOnlyAndContainsAllThreeComparisons() {
        val settings = settingsSource()
        assertTrue(settings.contains("Liquid Bottom Tabs Fixture"))
        assertTrue(settings.contains("DEBUG_LIQUID_BOTTOM_TABS_FIXTURE_ACTIVITY"))

        val manifest = listOf(
            File("android/app/src/debug/AndroidManifest.xml"),
            File("app/src/debug/AndroidManifest.xml"),
            File("src/debug/AndroidManifest.xml"),
        ).firstOrNull(File::isFile)?.readText()
            ?: error("debug AndroidManifest.xml not found from ${System.getProperty("user.dir")}")
        assertTrue(manifest.contains(".debug.LiquidBottomTabsFixtureActivity"))

        val fixture = debugSourceFile("debug/LiquidBottomTabsFixtureActivity.kt")
        assertTrue(fixture.contains("val backdrop = rememberLayerBackdrop()"))
        assertTrue(fixture.contains(".layerBackdrop(backdrop)"))
        assertTrue(fixture.contains("UpstreamLiquidBottomTabs"))
        assertTrue(fixture.contains("LiquidBottomTabs("))
        assertTrue(fixture.contains("tabsCount = 3"))
        assertTrue(fixture.contains("tabsCount = 2"))

        val upstreamTabs = debugSourceFile("liquidglass/UpstreamLiquidBottomTabs.kt")
        assertTrue(upstreamTabs.contains("lerp(1f, 1.2f, dampedDragAnimation.pressProgress)"))
        assertTrue(upstreamTabs.contains("rememberCombinedBackdrop(backdrop, tabsBackdrop)"))
        assertTrue(upstreamTabs.contains("ColorFilter.tint(accentColor)"))
    }

    private fun assertOrdered(source: String, vararg markers: String) {
        var previousIndex = -1
        markers.forEach { marker ->
            val index = source.indexOf(marker)
            assertTrue(index >= 0 && index > previousIndex)
            previousIndex = index
        }
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

    private fun debugSourceFile(name: String): String {
        val relative = "com/codexquotatray/android/$name"
        val candidates = listOf(
            File("android/app/src/debug/java/$relative"),
            File("app/src/debug/java/$relative"),
            File("src/debug/java/$relative"),
        )
        return candidates.firstOrNull(File::isFile)?.readText()
            ?: error("$name debug source not found from ${System.getProperty("user.dir")}")
    }
}
