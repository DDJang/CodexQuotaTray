package com.codexquotatray.android

import com.codexquotatray.android.source.DataSourcePriority
import com.codexquotatray.android.liquidglass.shouldCommitLiquidSegmentSelection
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
    fun settingsHomeUsesOAuthPairingAndNavigationStateLabels() {
        val source = settingsSource()

        assertTrue(source.contains("private val oauthStore by lazy { OAuthStore(this) }"))
        assertTrue(source.contains("private var codexLoggedIn by mutableStateOf(false)"))
        assertTrue(source.contains("codexLoggedIn = oauthStore.load() != null"))
        assertTrue(source.contains("trailing = if (codexLoggedIn) \"已登录\" else \"未登录\""))
        assertTrue(source.contains("trailing = pairing?.displayName ?: \"未配对\""))
        assertTrue(source.contains("SettingsNavigationRow(\"数据\")"))
        assertFalse(source.contains("SettingsNavigationRow(\"数据\", if (backgroundRefresh || tokenBackgroundSync)"))
    }

    @Test
    fun aboutUsesCenteredProjectAndLicenseLinksWithIndependentHaptics() {
        val about = sourceFile("AboutActivity.kt")
        val renderedAbout = about.substringBefore("private fun openProjectPage()")

        assertTrue(renderedAbout.contains("GitHub 项目主页"))
        assertTrue(renderedAbout.contains("开源许可证(MIT)"))
        assertTrue(renderedAbout.contains("modifier = Modifier.padding(top = 18.dp)"))
        assertTrue(renderedAbout.contains("horizontalArrangement = Arrangement.spacedBy(18.dp)"))
        assertTrue(renderedAbout.contains("verticalAlignment = Alignment.CenterVertically"))
        assertTrue(renderedAbout.contains("rememberSystemHapticClick(::openProjectPage)"))
        assertTrue(renderedAbout.contains("rememberSystemHapticClick(::openLicensePage)"))
        assertFalse(renderedAbout.contains("PROJECT_URL"))
        assertTrue(about.contains("Uri.parse(PROJECT_URL)"))
        assertTrue(about.contains("Uri.parse(LICENSE_URL)"))
        assertTrue(about.contains("private const val PROJECT_URL = \"https://github.com/DDJang/CodexQuotaTray\""))
        assertTrue(about.contains("private const val LICENSE_URL = \"https://github.com/DDJang/CodexQuotaTray/blob/main/LICENSE\""))
    }

    @Test
    fun segmentedSelectorUsesFullSlotBackdropWithInsetVisibleControl() {
        val source = sourceFile("SettingsUi.kt")
        val selector = source.substringAfter("internal fun SettingsSegmentedSelector(")
            .substringBefore("internal data class SettingsSegmentOption")
        assertTrue(selector.contains("val horizontalInset = SettingsUiTokens.actionHorizontalInset"))
        assertTrue(selector.contains("val verticalInset = SettingsUiTokens.segmentedBottomInset"))
        assertTrue(selector.contains("val controlHeight = SettingsUiTokens.segmentedHeight"))
        assertTrue(selector.contains(".height(controlHeight + verticalInset * 2)"))
        assertTrue(selector.contains(".fillMaxSize()"))
        assertTrue(
            selector.contains(
                ".fillMaxSize()\n                .alpha(0f)\n                .layerBackdrop(segmentedBackdrop)",
            ),
        )
        assertTrue(selector.contains(".layerBackdrop(segmentedBackdrop)"))
        assertTrue(selector.contains(".background(palette.color(palette.surface))"))
        assertTrue(selector.contains(".align(Alignment.Center)"))
        assertTrue(selector.contains(".padding(horizontal = horizontalInset)"))
        assertTrue(selector.contains(".height(controlHeight)"))
        assertFalse(selector.contains("top = SettingsUiTokens.segmentedBottomInset"))
        assertFalse(selector.contains("bottom = SettingsUiTokens.segmentedBottomInset"))
    }

    @Test
    fun segmentedSelectorMapsBusinessValuesToStableIndices() {
        val options = listOf(
            SettingsSegmentOption(24, "1 day"),
            SettingsSegmentOption(6, "6 hours"),
            SettingsSegmentOption(1, "1 hour"),
        )

        assertEquals(0, settingsSegmentIndex(options, 24))
        assertEquals(1, settingsSegmentIndex(options, 6))
        assertEquals(2, settingsSegmentIndex(options, 1))
        assertEquals(0, settingsSegmentIndex(options, 999))
        assertEquals(24, settingsSegmentValue(options, 0))
        assertEquals(1, settingsSegmentValue(options, 2))
        assertEquals(null, settingsSegmentValue(options, 3))
    }

    @Test
    fun onlySegmentedSettingsGroupsAllowLiquidOverflow() {
        val settingsUi = sourceFile("SettingsUi.kt")
        val settingsActivity = sourceFile("SettingsActivity.kt")

        assertTrue(settingsUi.contains("allowLiquidOverflow: Boolean = false"))
        assertTrue(settingsUi.contains("if (allowLiquidOverflow)"))
        assertTrue(settingsUi.contains("Modifier.background("))
        assertTrue(settingsUi.contains("Card("))
        assertEquals(
            3,
            Regex("SettingsGroup\\(allowLiquidOverflow = true\\)")
                .findAll(settingsActivity)
                .count(),
        )
        assertTrue(
            Regex(
                "SettingsSection\\(stringResource\\(R\\.string\\.reset_credit_expiry_section\\)\\)\\s*\\{\\s*" +
                    "SettingsGroup\\(allowLiquidOverflow = true\\)",
            ).containsMatchIn(settingsActivity),
        )
        assertTrue(
            Regex("SettingsSection\\(\"额度\"\\)\\s*\\{\\s*SettingsGroup\\(allowLiquidOverflow = true\\)")
                .containsMatchIn(settingsActivity),
        )
        assertTrue(
            Regex("SettingsSection\\(\"统计\"\\)\\s*\\{\\s*SettingsGroup\\(allowLiquidOverflow = true\\)")
                .containsMatchIn(settingsActivity),
        )
    }

    @Test
    fun productionLiquidSegmentedTabsKeepFixtureGeometryAndGateDisabledSelection() {
        assertTrue(shouldCommitLiquidSegmentSelection(true, 0, 1))
        assertFalse(shouldCommitLiquidSegmentSelection(true, 1, 1))
        assertFalse(shouldCommitLiquidSegmentSelection(false, 0, 1))

        val source = sourceFile("liquidglass/LiquidSegmentedTabs.kt")
        val settings = sourceFile("SettingsUi.kt")

        assertTrue(source.contains("Adapted from Kyant0/AndroidLiquidGlass"))
        assertTrue(source.contains("SEGMENTED_SCALE = 0.75f"))
        assertTrue(source.contains("constraints.maxWidth"))
        assertTrue(source.contains("segmentedOuterHeight = 64.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedSelectedHeight = 56.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedOuterPadding = 4.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedPanelOffset = 4.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedBlurRadius = 8.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedLensSize = 24.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedSelectedLensWidth = 10.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedSelectedLensHeight = 14.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedInnerShadowRadius = 8.dp.scaledSegmented()"))
        assertTrue(source.contains("segmentedOuterPressDeformation = 16.dp.scaledSegmented()"))
        assertTrue(source.contains("fontSize = 14.sp"))
        assertTrue(source.contains("pressedScale = 78f / 56f"))
        assertTrue(source.contains("lerp(1f, 1.2f"))
        assertTrue(source.contains("chromaticAberration = true"))
        assertTrue(source.contains("enabledState"))
        assertTrue(source.contains("if (enabled) interactiveHighlight.gestureModifier else Modifier"))
        assertTrue(source.contains("if (enabled) dampedDragAnimation.modifier else Modifier"))
        assertFalse(source.contains("logicalWidth"))
        assertFalse(source.contains("requiredWidth"))
        assertFalse(source.contains("requiredHeight"))
        assertFalse(source.contains("TransformOrigin"))
        assertFalse(source.contains("scaleX = SEGMENTED_SCALE"))

        assertTrue(settings.contains("val segmentedBackdrop = rememberLayerBackdrop()"))
        assertTrue(settings.contains(".layerBackdrop(segmentedBackdrop)"))
        assertTrue(settings.contains(".background(palette.color(palette.surface))"))
        assertTrue(settings.contains("LiquidSegmentedTabs("))
        assertFalse(settings.contains("SettingsSegment("))
        assertFalse(settings.contains("background(if (selected) palette.color(palette.accent)"))
        assertFalse(settings.contains("pageBackdrop"))
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
        assertTrue(pageSource.contains(".background(backgroundColor)"))
        assertTrue(scaffold.contains("backdrop = pageBackdrop"))

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
    fun secondaryScreenBackdropSourceIncludesBackgroundAndScrollContent() {
        val source = sourceFile("CodexUi.kt")
        val sourceBlock = source.substringAfter("Box(modifier.fillMaxSize())")
            .substringBefore("SettingsGradientBlurHeader(")

        assertTrue(sourceBlock.contains(".layerBackdrop(backdrop)"))
        assertTrue(sourceBlock.contains(".background(backgroundColor)"))
        assertTrue(sourceBlock.contains(".verticalScroll(scrollState, overscrollEffect = null)"))
        assertTrue(sourceBlock.contains("content()"))
        assertTrue(source.contains("backdrop = backdrop"))
        assertFalse(source.contains("Box(modifier.fillMaxSize().background(palette.color(palette.background)))"))
    }

    @Test
    fun liquidMainDockUsesStableAdaptersForExternalSelectionState() {
        val source = sourceFile("GlassComponents.kt")
        val dock = source.substringAfter("internal fun LiquidMainDock(")

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
    fun glassRefreshActionMatchesBottomCapsuleAndSecondaryButtonsKeepOriginalSize() {
        val components = sourceFile("GlassComponents.kt")
        val main = sourceFile("MainActivity.kt")
        val settings = sourceFile("SettingsActivity.kt")
        val codexUi = sourceFile("CodexUi.kt")
        val about = sourceFile("AboutActivity.kt")
        val dock = components.substringAfter("internal fun LiquidMainDock(")

        assertTrue(components.contains("internal val glassActionButtonSize = 64.dp"))
        assertTrue(components.contains("internal val glassRefreshIconSize = 28.dp"))
        assertTrue(dock.contains("val actionSize = glassActionButtonSize"))
        assertTrue(dock.contains("val navigationHeight = glassActionButtonSize"))
        assertTrue(dock.contains("iconSize = glassRefreshIconSize"))
        assertTrue(main.contains("buttonSize = 52.dp"))
        assertTrue(main.contains("iconSize = 24.dp"))
        assertTrue(settings.contains("buttonSize = 52.dp"))
        assertTrue(settings.contains("iconSize = 25.dp"))
        assertTrue(settings.contains("Spacer(Modifier.size(52.dp))"))
        assertTrue(codexUi.contains("buttonSize = 52.dp"))
        assertTrue(codexUi.contains("iconSize = 25.dp"))
        assertTrue(codexUi.contains("Spacer(Modifier.size(52.dp))"))
        assertTrue(about.contains("buttonSize = 52.dp"))
        assertTrue(about.contains("iconSize = 25.dp"))
        assertTrue(about.contains("Spacer(Modifier.size(52.dp))"))
        assertFalse(main.contains("glassActionButtonSize"))
        assertFalse(settings.contains("glassActionButtonSize"))
        assertFalse(codexUi.contains("glassActionButtonSize"))
        assertFalse(about.contains("glassActionButtonSize"))
    }

    @Test
    fun liquidIconButtonsUseUpstreamPipelineAndFixtureCoversTopologyCases() {
        val component = sourceFile("LiquidIconButton.kt")
        val components = sourceFile("GlassComponents.kt")
        val main = sourceFile("MainActivity.kt")
        val settings = sourceFile("SettingsActivity.kt")
        val codexUi = sourceFile("CodexUi.kt")
        val fixture = debugSourceFile("debug/LiquidIconButtonFixtureActivity.kt")
        val manifest = debugManifestSource()
        val dock = components.substringAfter("internal fun LiquidMainDock(")

        assertTrue(component.contains("shape = { Capsule() }"))
        assertTrue(component.contains("vibrancy()"))
        assertTrue(component.contains("blur(2f.dp.toPx())"))
        assertTrue(component.contains("lens(12f.dp.toPx(), 24f.dp.toPx())"))
        assertTrue(component.contains("val scale = lerp(1f, 1f + 4f.dp.toPx() / size.height, progress)"))
        assertTrue(component.contains("val maxOffset = size.minDimension"))
        assertTrue(component.contains("val initialDerivative = 0.05f"))
        assertTrue(component.contains("translationX = maxOffset * tanh(initialDerivative * offset.x / maxOffset)"))
        assertTrue(component.contains("translationY = maxOffset * tanh(initialDerivative * offset.y / maxOffset)"))
        assertTrue(component.contains("onDrawSurface = {},"))
        assertFalse(component.contains("GlassSurface("))
        assertFalse(component.contains("Highlight.Default"))
        assertFalse(component.contains("colorControls"))
        assertFalse(component.contains("surfaceAlpha"))
        assertFalse(component.contains(".clip("))

        assertTrue(dock.contains("LiquidIconButton("))
        assertFalse(dock.contains("GlassIconButton("))
        assertTrue(main.contains("LiquidIconButton("))
        assertFalse(main.contains("GlassIconButton("))
        assertTrue(settings.contains("LiquidIconButton("))
        assertFalse(settings.contains("GlassIconButton("))
        assertTrue(codexUi.contains("LiquidIconButton("))
        assertFalse(codexUi.contains("GlassIconButton("))

        assertTrue(fixture.contains("UpstreamLiquidIconButton"))
        assertTrue(fixture.contains("LiquidIconButton("))
        assertTrue(fixture.contains("Case A · current Settings topology"))
        assertTrue(fixture.contains("Case B · background enters source"))
        assertTrue(fixture.contains("Case C · rich backdrop control"))
        assertTrue(fixture.contains("sourceIncludesBackground = false"))
        assertTrue(fixture.contains("sourceIncludesBackground = true"))
        assertTrue(fixture.contains("SettingsLikeScrollContent"))
        assertTrue(fixture.contains("账号与配对"))
        assertTrue(fixture.contains(".layerBackdrop(backdrop)"))
        assertTrue(manifest.contains(".debug.LiquidIconButtonFixtureActivity"))
    }

    @Test
    fun legacyLiquidDockImplementationIsRemovedAfterKyantMigration() {
        val components = sourceFile("GlassComponents.kt")
        val animations = sourceFile("BottomDockAnimations.kt")

        assertFalse(components.contains("LiquidTabCapsule"))
        assertFalse(components.contains("LocalDockTabScale"))
        assertFalse(components.contains("BottomDockDampedDragAnimation"))
        assertFalse(animations.contains("BottomDockDampedDragAnimation"))
        assertFalse(animations.contains("isDockDrag"))
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

    @Test
    fun allLiquidFixturesAreReachableFromDebugDeveloperOptions() {
        val settings = settingsSource()
        val developerOptions = settings.substringAfter("SettingsSection(\"开发者选项\")")
            .substringBefore("private fun openDebugQuotaRingFixture")
        assertTrue(developerOptions.contains("Quota Ring Fixture"))
        assertTrue(developerOptions.contains("Liquid Bottom Tabs Fixture"))
        assertTrue(developerOptions.contains("Liquid Icon Button Fixture"))
        assertTrue(developerOptions.contains("Liquid Segmented Fixture"))
        assertTrue(settings.contains("DEBUG_LIQUID_ICON_BUTTON_FIXTURE_ACTIVITY"))
        assertTrue(settings.contains("DEBUG_LIQUID_SEGMENTED_FIXTURE_ACTIVITY"))
        assertTrue(settings.contains("openDebugLiquidIconButtonFixture"))
        assertTrue(settings.contains("openDebugLiquidSegmentedFixture"))

        val manifest = debugManifestSource()
        assertTrue(manifest.contains(".debug.LiquidIconButtonFixtureActivity"))
        val segmentedManifestEntry = manifest
            .substringAfter(".debug.LiquidSegmentedFixtureActivity")
            .substringBefore("</activity>")
        assertTrue(segmentedManifestEntry.contains("android:exported=\"false\""))

        val fixture = debugSourceFile("debug/LiquidSegmentedFixtureActivity.kt")
        val adaptation = debugSourceFile("debug/KyantLiquidSegmentedAdaptation.kt")
        assertTrue(fixture.contains("Production topology regression · source bounds only"))
        assertTrue(fixture.contains("A · exact-bounds local source"))
        assertTrue(fixture.contains("B · full-slot local source"))
        assertTrue(
            fixture.contains(
                "private val resetCreditExpiryLeadLabels = listOf(\"1 天\", \"6 小时\", \"1 小时\")",
            ),
        )
        assertTrue(fixture.contains("fullSlotSource = false"))
        assertTrue(fixture.contains("fullSlotSource = true"))
        assertTrue(fixture.contains("val sourceModifier = if (fullSlotSource)"))
        assertTrue(fixture.contains("controlHeight + verticalInset * 2"))
        assertTrue(fixture.contains("LiquidSegmentedTabs("))
        assertTrue(fixture.contains("Ancestor clip regression · full-slot source held constant"))
        assertTrue(fixture.contains("A · Material Card ancestor · clips"))
        assertTrue(fixture.contains("B · rounded background ancestor · non-clipping"))
        assertTrue(fixture.contains("materialCardAncestor = true"))
        assertTrue(fixture.contains("materialCardAncestor = false"))
        assertTrue(fixture.contains("CardDefaults.cardColors(containerColor = surfaceColor)"))
        assertTrue(fixture.contains(".background(surfaceColor, groupShape)"))
        assertTrue(fixture.contains("AncestorClipRegressionContent("))
        assertTrue(fixture.contains("SettingsSegmentedSelector("))
        assertTrue(fixture.contains("KyantLiquidSegmentedAdaptation("))
        assertTrue(fixture.contains("Kyant geometry adaptation · 0.75"))
        assertTrue(fixture.contains("sourcePriorityLabels"))
        assertTrue(fixture.contains("refreshIntervalLabels"))
        assertTrue(fixture.contains("remember { mutableIntStateOf(0) }"))
        assertTrue(fixture.contains("selected = \$selectedLabel"))
        assertTrue(fixture.contains("settingsLikeDarkSurface"))
        assertTrue(fixture.contains(".layerBackdrop(backdrop)"))
        assertTrue(fixture.contains(".background(Color.Black)"))
        assertFalse(fixture.contains("LiquidSegmentedSelector"))
        assertTrue(adaptation.contains("SEGMENTED_SCALE = 0.75f"))
        assertTrue(adaptation.contains(".fillMaxWidth()"))
        assertTrue(adaptation.contains(".height(segmentedOuterHeight)"))
        assertTrue(adaptation.contains("constraints.maxWidth"))
        assertTrue(adaptation.contains("segmentedOuterHeight = 64.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedSelectedHeight = 56.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedOuterPadding = 4.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedBlurRadius = 8.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedLensSize = 24.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedSelectedLensWidth = 10.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedSelectedLensHeight = 14.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedInnerShadowRadius = 8.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("segmentedOuterPressDeformation = 16.dp.scaledSegmented()"))
        assertTrue(adaptation.contains("fontSize = 14.sp"))
        assertTrue(adaptation.contains("var requestedIndex by remember"))
        assertTrue(adaptation.contains("LaunchedEffect(selectedIndex)"))
        assertTrue(adaptation.contains("HapticFeedbackType.ContextClick"))
        assertTrue(adaptation.contains("committedSelectedIndex.value != index"))
        assertTrue(adaptation.contains("pressedScale = 78f / 56f"))
        assertTrue(adaptation.contains("lerp(1f, 1.2f"))
        assertTrue(adaptation.contains("chromaticAberration = true"))
        assertFalse(adaptation.contains("logicalWidth"))
        assertFalse(adaptation.contains("requiredWidth"))
        assertFalse(adaptation.contains("requiredHeight"))
        assertFalse(adaptation.contains("TransformOrigin"))
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

    private fun debugManifestSource(): String {
        val candidates = listOf(
            File("android/app/src/debug/AndroidManifest.xml"),
            File("app/src/debug/AndroidManifest.xml"),
            File("src/debug/AndroidManifest.xml"),
        )
        return candidates.firstOrNull(File::isFile)?.readText()
            ?: error("debug AndroidManifest.xml not found from ${System.getProperty("user.dir")}")
    }
}
