package com.codexquotatray.android

import androidx.compose.ui.geometry.Offset
import com.codexquotatray.android.source.DataSourcePriority
import com.codexquotatray.android.liquidglass.liquidBottomTabPreviewHighlightProgress
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
    fun actionAndSegmentedSettingsGroupsAllowLiquidOverflow() {
        val settingsUi = sourceFile("SettingsUi.kt")
        val settingsActivity = sourceFile("SettingsActivity.kt")

        assertTrue(settingsUi.contains("allowLiquidOverflow: Boolean = false"))
        assertTrue(settingsUi.contains("if (allowLiquidOverflow)"))
        assertTrue(settingsUi.contains("Modifier.background("))
        assertTrue(settingsUi.contains("Card("))
        assertEquals(
            5,
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
        assertTrue(
            Regex("private fun ColumnScope.TokenPairingSettings\\(\\)[\\s\\S]*?" +
                "SettingsGroup\\(allowLiquidOverflow = true\\)")
                .containsMatchIn(settingsActivity),
        )
        assertTrue(
            Regex("SettingsSection\\(\"版本\"\\)\\s*\\{\\s*SettingsGroup\\(allowLiquidOverflow = true\\)")
                .containsMatchIn(settingsActivity),
        )

        listOf("AccountActivity.kt", "LoginActivity.kt", "LogActivity.kt").forEach { fileName ->
            assertTrue(sourceFile(fileName).contains("SettingsGroup(allowLiquidOverflow = true)"))
        }
    }

    @Test
    fun liquidActionButtonStyleUsesDangerPrecedenceAndExpectedSemantics() {
        assertEquals(SettingsActionButtonStyle.NEUTRAL, settingsActionButtonStyle(false, false))
        assertEquals(SettingsActionButtonStyle.PRIMARY, settingsActionButtonStyle(true, false))
        assertEquals(SettingsActionButtonStyle.DANGER, settingsActionButtonStyle(false, true))
        assertEquals(SettingsActionButtonStyle.DANGER, settingsActionButtonStyle(true, true))
    }

    @Test
    fun updateStatusDisplayPrioritizesCheckingResultHistoryAndNeverChecked() {
        val neverChecked = updateStatusDisplay("尚未检查", false, 0L, "")
        assertEquals("未检查", neverChecked)

        val historical = updateStatusDisplay("尚未检查", false, 1L, "09-02 16:20")
        assertEquals("上次检查时间为 09-02 16:20", historical)

        val checking = updateStatusDisplay("已是最新版本 0.11.2", true, 1L, "09-02 16:20")
        assertEquals("正在检查…", checking)

        val upToDate = updateStatusDisplay("已是最新版本 0.11.2", false, 1L, "09-02 16:20")
        assertEquals("已是最新版本 0.11.2", upToDate)

        val failed = updateStatusDisplay("检查更新失败：fixture", false, 2L, "09-02 16:21")
        assertEquals("检查更新失败：fixture", failed)

        val available = updateStatusDisplay("发现新版本 0.11.3", false, 3L, "09-02 16:22")
        assertEquals("发现新版本 0.11.3", available)

        listOf(neverChecked, historical, checking, upToDate, failed, available).forEach {
            assertFalse(it.contains('\n'))
        }
    }

    @Test
    fun liquidActionButtonKeepsKyantGeometryAndBoundsOnlyVisualOffset() {
        assertEquals(
            Offset(48f, -48f),
            boundedLiquidActionOffset(Offset(240f, -240f), 48f),
        )
        assertEquals(Offset.Zero, boundedLiquidActionOffset(Offset(240f, -240f), 0f))

        val source = sourceFile("LiquidActionButton.kt")
        assertTrue(source.contains("Adapted and modified from Kyant0/AndroidLiquidGlass"))
        assertTrue(source.contains("shape = { Capsule() }"))
        assertTrue(source.contains("vibrancy()"))
        assertTrue(source.contains("blur(2f.dp.toPx())"))
        assertTrue(source.contains("lens(12f.dp.toPx(), 24f.dp.toPx())"))
        assertTrue(source.contains("interactiveHighlight.offset"))
        assertTrue(source.contains("boundedLiquidActionOffset("))
        assertTrue(source.contains("size.minDimension"))
        assertTrue(source.contains("layerBlock = {"))
        assertFalse(source.contains("layerBlock = if (enabled)"))
        assertTrue(source.contains(".then(interactiveHighlight.modifier)"))
        assertTrue(source.contains(".then(if (enabled) interactiveHighlight.gestureModifier else Modifier)"))
        assertTrue(source.contains(".height(48f.dp)"))
        assertTrue(source.contains(".padding(horizontal = 16f.dp)"))
        assertTrue(source.contains("Arrangement.spacedBy(8f.dp, Alignment.CenterHorizontally)"))
        assertTrue(source.contains("role = Role.Button"))
        assertTrue(source.contains("enabled = enabled"))

        val settings = sourceFile("SettingsUi.kt")
        val action = settings.substringAfter("internal fun SettingsActionButton(")
            .substringBefore("internal fun SettingsSegmentedSelector(")
        assertTrue(action.contains("val actionBackdrop = rememberLayerBackdrop()"))
        assertTrue(action.contains(".fillMaxSize()"))
        assertTrue(action.contains(".layerBackdrop(actionBackdrop)"))
        assertTrue(action.contains("LiquidActionButton("))
        assertTrue(action.contains("palette.color(palette.accent)"))
        assertTrue(action.contains("CodexColors.danger"))
        assertTrue(action.contains("palette.color(palette.body)"))
        assertFalse(action.contains("busy: Boolean = false"))
        assertFalse(action.contains("CircularProgressIndicator("))
        assertTrue(action.contains(".height(SettingsUiTokens.actionHeight + topPadding + bottomPadding)"))
        assertFalse(action.contains("ButtonDefaults"))

        val settingsActivity = sourceFile("SettingsActivity.kt")
        assertTrue(settingsActivity.contains("label = if (updateChecking) \"正在检查…\" else \"检查更新\""))
        assertFalse(settingsActivity.contains("busy = updateChecking"))
        assertTrue(settingsActivity.contains("updateStatusDisplay("))
        assertTrue(settingsActivity.contains("checking = updateChecking"))
        assertTrue(settingsActivity.contains("valueMaxLines = 1"))
        assertTrue(settingsActivity.contains("val presentationStartedAt = SystemClock.elapsedRealtime()"))
        assertTrue(settingsActivity.contains("val finishedAt = SystemClock.elapsedRealtime()"))
        assertTrue(settingsActivity.contains("remainingRefreshPresentationMillis("))
        assertTrue(settingsActivity.contains("updateMain.postDelayed"))
        assertFalse(settingsActivity.contains("SettingsInfoRow(\"上次检查\""))
    }

    @Test
    fun liquidActionButtonFixtureKeepsMaterialUpstreamAndBoundedComparisons() {
        val fixture = debugSourceFile("debug/LiquidActionButtonFixtureActivity.kt")
        assertTrue(fixture.contains("CurrentMaterialButton"))
        assertTrue(fixture.contains("private fun BoundedLiquidButton("))
        assertTrue(fixture.contains("Exact Kyant upstream"))
        assertTrue(fixture.contains("Production candidate · bounded drag"))
        assertTrue(fixture.contains("StateMutationRegressionSection"))
        assertTrue(fixture.contains("label = if (checking) \"正在检查…\" else \"检查更新\""))
        assertTrue(fixture.contains("enabled = !checking"))
        assertTrue(fixture.contains("delay(1500)"))
        assertTrue(fixture.contains("val maxOffset = size.minDimension"))
        assertTrue(fixture.contains("rawOffset = interactiveHighlight.offset"))
        assertTrue(fixture.contains("rawOffset.x.fastCoerceIn(-maxOffset, maxOffset)"))
    }

    @Test
    fun liquidIconButtonKeepsReleaseLayerWhenItsActionBecomesBusy() {
        val source = sourceFile("LiquidIconButton.kt")
        assertTrue(source.contains("layerBlock = {"))
        assertFalse(source.contains("layerBlock = if (enabled)"))
        assertTrue(source.contains(".then(interactiveHighlight.modifier)"))
        assertTrue(source.contains(".then(if (enabled) interactiveHighlight.gestureModifier else Modifier)"))

        val dock = sourceFile("GlassComponents.kt")
        assertTrue(dock.contains("enabled = actionEnabled && !actionBusy"))
        assertTrue(dock.contains("busy = actionBusy"))
    }

    @Test
    fun productionLiquidSegmentedTabsKeepFixtureGeometryAndGateDisabledSelection() {
        assertTrue(shouldCommitLiquidSegmentSelection(true, 0, 1))
        assertFalse(shouldCommitLiquidSegmentSelection(true, 1, 1))
        assertFalse(shouldCommitLiquidSegmentSelection(false, 0, 1))

        val source = sourceFile("liquidglass/LiquidSegmentedTabs.kt")
        val settings = sourceFile("SettingsUi.kt")

        assertTrue(source.contains("Adapted and modified from Kyant0/AndroidLiquidGlass"))
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
        assertTrue(dock.contains("LiquidBottomTab(tabIndex = 0, onClick = { requestedIndex = 0 })"))
        assertTrue(dock.contains("LiquidBottomTab(tabIndex = 1, onClick = { requestedIndex = 1 })"))
    }

    @Test
    fun glassRefreshActionUsesPolishedSizesAndKeepsBottomGeometry() {
        val components = sourceFile("GlassComponents.kt")
        val main = sourceFile("MainActivity.kt")
        val settings = sourceFile("SettingsActivity.kt")
        val codexUi = sourceFile("CodexUi.kt")
        val about = sourceFile("AboutActivity.kt")
        val dock = components.substringAfter("internal fun LiquidMainDock(")

        assertTrue(components.contains("internal val glassActionButtonSize = 64.dp"))
        assertTrue(components.contains("internal val glassRefreshIconSize = 32.dp"))
        assertTrue(dock.contains("val actionSize = glassActionButtonSize"))
        assertTrue(dock.contains("val navigationHeight = glassActionButtonSize"))
        assertTrue(dock.contains("iconSize = glassRefreshIconSize"))
        assertTrue(main.contains("buttonSize = 48.dp"))
        assertTrue(main.contains("iconSize = 24.dp"))
        assertTrue(settings.contains("buttonSize = 48.dp"))
        assertTrue(settings.contains("iconSize = 25.dp"))
        assertTrue(settings.contains("Spacer(Modifier.size(48.dp))"))
        assertTrue(codexUi.contains("buttonSize = 48.dp"))
        assertTrue(codexUi.contains("iconSize = 25.dp"))
        assertTrue(codexUi.contains("Spacer(Modifier.size(48.dp))"))
        assertTrue(about.contains("buttonSize = 48.dp"))
        assertTrue(about.contains("iconSize = 25.dp"))
        assertTrue(about.contains("Spacer(Modifier.size(48.dp))"))
        assertFalse(main.contains("buttonSize = 52.dp"))
        assertFalse(settings.contains("buttonSize = 52.dp"))
        assertFalse(codexUi.contains("buttonSize = 52.dp"))
        assertFalse(about.contains("buttonSize = 52.dp"))
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
    fun interactiveHighlightsKeepTheOriginalDirectGestureModel() {
        val dockAnimations = sourceFile("BottomDockAnimations.kt")
        val interactiveHighlight = sourceFile("liquidglass/InteractiveHighlight.kt")

        assertTrue(dockAnimations.contains("positionUpdater.submit(generation, change.position)"))
        assertTrue(interactiveHighlight.contains("positionUpdater.submit(generation, change.position)"))
        assertTrue(interactiveHighlight.contains("position(size, positionAnimation.value)"))
        assertTrue(interactiveHighlight.contains("externalProgress"))
        assertTrue(interactiveHighlight.contains("drawInteractiveHighlight"))
        assertTrue(dockAnimations.contains("invalidatePositionUpdates()"))
        assertTrue(interactiveHighlight.contains("invalidatePositionUpdates()"))
        assertFalse(interactiveHighlight.contains("fun pressAt("))
        assertFalse(interactiveHighlight.contains("fun prepareAt("))
        assertFalse(interactiveHighlight.contains("fun reveal("))
        assertFalse(interactiveHighlight.contains("fun cancelPrepared("))
        assertFalse(interactiveHighlight.contains("fun moveTo("))
        assertFalse(interactiveHighlight.contains("fun release("))
        assertFalse(interactiveHighlight.contains("fun cancel("))
        assertFalse(interactiveHighlight.contains("usesExternalPosition"))
        assertFalse(interactiveHighlight.contains("visible"))
        assertFalse(
            dockAnimations.contains(
                "animationScope.launch { positionAnimation.snapTo(change.position) }",
            ),
        )
        assertFalse(
            interactiveHighlight.contains(
                "animationScope.launch { positionAnimation.snapTo(change.position) }",
            ),
        )
    }

    @Test
    fun mainPageUsesTheBaselineAnimatedContentInsideTheStaticChromeBackdrop() {
        val source = sourceFile("MainActivity.kt")
        val chrome = source.substringAfter("val chromeBackdrop = rememberLayerBackdrop")
            .substringBefore("Box(Modifier.align(Alignment.TopEnd)")
        val staticSource = chrome.substringBefore("Column(")
        val dynamicPage = chrome.substringAfter("Column(")

        assertTrue(staticSource.contains(".layerBackdrop(chromeBackdrop)"))
        assertTrue(staticSource.contains(".background(palette.color(palette.background))"))
        assertFalse(staticSource.contains("AnimatedContent("))
        assertTrue(dynamicPage.contains("AnimatedContent("))
        assertTrue(dynamicPage.contains("fadeIn(animationSpec = tween(200))"))
        assertTrue(dynamicPage.contains("initialOffsetX = { width -> direction * width / 20 }"))
        assertTrue(dynamicPage.contains("fadeOut(animationSpec = tween(160))"))
        assertTrue(dynamicPage.contains("targetOffsetX = { width -> -direction * width / 28 }"))
        assertFalse(source.contains("MainPageSwitcher("))
        assertTrue(source.contains("if (targetIndex == selectedIndex) return"))
        assertTrue(source.contains("backdrop = chromeBackdrop"))
    }

    @Test
    fun dampedDragSeparatesDragVelocityFromProgrammaticSettling() {
        val source = sourceFile("liquidglass/DampedDragAnimation.kt")
        val updateValue = source.substringAfter("fun updateValue(")
            .substringBefore("fun settleToValue(")
        val settleToValue = source.substringAfter("fun settleToValue(")
            .substringBefore("fun animateToValue(")

        assertTrue(updateValue.contains("updateVelocity()"))
        assertTrue(settleToValue.contains("valueAnimation.animateTo"))
        assertTrue(settleToValue.contains("valueAnimationSpec"))
        assertTrue(settleToValue.contains("velocityAnimation.animateTo(0f, velocityAnimationSpec)"))
        assertFalse(settleToValue.contains("updateVelocity()"))
        assertFalse(settleToValue.contains("press()"))
        assertFalse(settleToValue.contains("release()"))
        assertFalse(settleToValue.contains("mutatorMutex"))
    }

    @Test
    fun previewHighlightFollowsActualPillDistanceAndPressProgress() {
        assertEquals(0f, liquidBottomTabPreviewHighlightProgress(0f, 1, 1f), 0f)
        assertEquals(1f, liquidBottomTabPreviewHighlightProgress(1f, 1, 1f), 0f)
        assertEquals(
            0.5f,
            liquidBottomTabPreviewHighlightProgress(0.265f, 0, 1f),
            0.0001f,
        )
        assertEquals(0.25f, liquidBottomTabPreviewHighlightProgress(0f, 0, 0.25f), 0f)
        assertEquals(0f, liquidBottomTabPreviewHighlightProgress(0f, 0, 0f), 0f)
    }

    @Test
    fun liquidBottomTabsKeepTheProductionCombinedBackdropRenderGraph() {
        val source = sourceFile("liquidglass/LiquidBottomTabs.kt")
        val tab = sourceFile("liquidglass/LiquidBottomTab.kt")

        assertTrue(source.contains("val tabsBackdrop = rememberLayerBackdrop()"))
        assertTrue(source.contains("CompositionLocalProvider"))
        assertTrue(source.contains("lerp(1f, 1.2f, dampedDragAnimation.pressProgress)"))
        assertTrue(source.contains(".alpha(0f)"))
        assertTrue(source.contains(".layerBackdrop(tabsBackdrop)"))
        assertTrue(source.contains("ColorFilter.tint(accentColor)"))
        assertTrue(source.contains("backdrop = rememberCombinedBackdrop(backdrop, tabsBackdrop)"))
        assertTrue(source.contains("tabsBackdrop"))
        assertTrue(source.contains("backdrop = backdrop"))
        assertTrue(source.contains("chromaticAberration = true"))
        assertTrue(source.contains("indicatorRefractionHeight: Dp = 11.dp"))
        assertTrue(source.contains("indicatorRefractionAmount: Dp = 18.dp"))
        assertFalse(source.contains("indicatorRefractionHeight: Dp = 10.dp"))
        assertFalse(source.contains("indicatorRefractionAmount: Dp = 14.dp"))
        assertTrue(source.contains("indicatorRefractionHeight.toPx() * progress"))
        assertTrue(source.contains("indicatorRefractionAmount.toPx() * progress"))
        assertTrue(source.contains("pressedScale = 78f / 56f"))
        assertTrue(source.contains("InteractiveHighlight("))
        assertTrue(source.contains("InteractiveHighlightHandoff("))
        assertTrue(source.contains("DampedDragAnimation("))
        assertTrue(source.contains("var committedIndex"))
        assertTrue(source.contains("var previewIndex"))
        assertTrue(source.contains("val visualTargetIndex = previewIndex ?: committedIndex"))
        assertTrue(source.contains("LocalLiquidBottomTabInteraction provides interactionCallbacks"))
        assertTrue(source.contains("dampedDragAnimation.press()"))
        assertTrue(source.contains("dampedDragAnimation.settleToValue(visualTargetIndex.toFloat())"))
        assertTrue(source.contains("dampedDragAnimation.settleToValue(committed.toFloat())"))
        assertTrue(source.contains("dampedDragAnimation.settleToValue(committedIndex.toFloat())"))
        assertTrue(source.contains("handoffHighlight.begin("))
        assertTrue(source.contains("handoffHighlight.progress(dampedDragAnimation.pressProgress)"))
        assertTrue(source.contains("handoffHighlight.fadeFrom(previewProgress)"))
        assertTrue(source.contains("liquidBottomTabPreviewHighlightProgress("))
        assertTrue(source.contains("Highlight.Default.copy(alpha = progress)"))
        assertTrue(source.contains(".then(interactiveHighlight.gestureModifier)"))
        assertFalse(source.contains("longPressHighlight"))
        assertFalse(source.contains("longPressJob"))
        assertFalse(source.contains("longPressTimeout"))
        assertFalse(source.contains("LocalViewConfiguration"))
        assertFalse(source.contains("prepareAt"))
        assertFalse(source.contains("interactiveHighlight.pressAt("))
        assertFalse(source.contains("interactiveHighlight.reveal("))
        assertFalse(source.contains("interactiveHighlight.moveTo("))
        val previewCallbacks = source.substringAfter("val interactionCallbacks =")
            .substringBefore("val interactiveHighlight =")
        assertFalse(previewCallbacks.contains("updateValue("))
        assertTrue(previewCallbacks.contains("dampedDragAnimation.settleToValue(visualTargetIndex.toFloat())"))
        assertFalse(previewCallbacks.contains("interactiveHighlight.pressAt("))
        assertFalse(previewCallbacks.contains("interactiveHighlight.reveal("))
        assertFalse(previewCallbacks.contains("interactiveHighlight.moveTo("))
        assertTrue(source.contains("applyBottomTabDragDelta("))
        val dragDeltaHelper = source.substringAfter(
            "private fun DampedDragAnimation.applyBottomTabDragDelta(",
        )
        assertTrue(dragDeltaHelper.contains("if (fromCurrentValue) value else targetValue"))
        assertTrue(dragDeltaHelper.contains("updateValue("))
        assertTrue(source.contains("fromCurrentValue = handoffDragNeedsCurrentValue"))

        val interactionProvider = source.substringAfter(
            "LocalLiquidBottomTabInteraction provides interactionCallbacks",
        )
        val visibleRows = interactionProvider.substringBefore("LocalLiquidBottomTabScale provides")
        val hiddenCaptureRows = interactionProvider.substringAfter("LocalLiquidBottomTabScale provides")
        assertTrue(visibleRows.contains("content = content"))
        assertFalse(visibleRows.contains("LocalLiquidBottomTabScale"))
        assertTrue(hiddenCaptureRows.contains(".alpha(0f)"))
        assertTrue(hiddenCaptureRows.contains(".layerBackdrop(tabsBackdrop)"))
        assertTrue(source.contains("onDragStart = { index ->"))
        assertTrue(source.contains("onDragEnd = { index ->"))
        assertTrue(source.contains("onDragCancel = { index ->"))
        assertTrue(source.contains("onDragCancelled = {"))
        val handoffDragEnd = source.substringAfter("onDragEnd = { index ->")
            .substringBefore("onDragCancel = { index ->")
        assertTrue(handoffDragEnd.contains("dampedDragAnimation.settleToValue(targetIndex.toFloat())"))
        assertTrue(handoffDragEnd.contains("onTabSelected(targetIndex)"))
        assertFalse(handoffDragEnd.contains("animateToValue("))
        val clickableCancel = source.substringAfter("onCancel = { index, press ->")
            .substringBefore("onClick = { index ->")
        val handoffCancel = clickableCancel.substringAfter(
            "val isHandoffDrag = dragInProgress && handoffDragIndex == index",
        ).substringBefore("if (!isHandoffDrag)")
        assertFalse(handoffCancel.contains("settleToValue("))
        assertFalse(handoffCancel.contains("release()"))
        val trueDragCancel = source.substringAfter("onDragCancelled = {")
            .substringBefore("onDrag = { _, dragAmount ->")
        assertTrue(trueDragCancel.contains("settleToValue(committedIndex.toFloat())"))
        assertTrue(trueDragCancel.contains("offsetAnimation.animateTo("))
        assertTrue(source.contains(".then(dampedDragAnimation.modifier)"))
        assertTrue(source.contains("onRelease = { index, press ->"))
        assertTrue(source.contains("onCancel = { index, press ->"))
        assertTrue(source.contains("onTabSelected(targetIndex)"))

        val productionComponents = sourceFile("GlassComponents.kt")
            .substringAfter("internal fun LiquidMainDock(")
        assertFalse(productionComponents.contains("indicatorRefractionHeight"))
        assertFalse(productionComponents.contains("indicatorRefractionAmount"))

        assertTrue(tab.contains("clickable("))
        assertTrue(tab.contains("interactionSource = interactionSource"))
        assertTrue(tab.contains("MutableInteractionSource"))
        assertTrue(tab.contains("PressInteraction.Press"))
        assertTrue(tab.contains("PressInteraction.Release"))
        assertTrue(tab.contains("PressInteraction.Cancel"))
        assertTrue(tab.contains("role = Role.Tab"))
        assertTrue(tab.contains("detectDragGestures"))
        assertTrue(tab.contains("pointerInput(tabIndex)"))
        assertTrue(tab.contains("onDragStart = {"))
        assertTrue(tab.contains("val onDrag: (index: Int, dragAmountX: Float)"))
        assertTrue(tab.contains("onDrag(tabIndex, dragAmount.x)"))
        assertFalse(tab.contains("change.position"))
        assertTrue(tab.contains("dragClaimed"))
        assertFalse(tab.contains("detectTapGestures"))
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
    fun liquidBottomTabsFixtureIsDebugOnlyAndContainsAllFiveComparisons() {
        val settings = settingsSource()
        assertTrue(settings.contains("Liquid Bottom Tabs Fixture"))
        assertTrue(settings.contains("DEBUG_LIQUID_BOTTOM_TABS_FIXTURE_ACTIVITY"))

        val manifest = listOf(
            File("android/app/src/debug/AndroidManifest.xml"),
            File("app/src/debug/AndroidManifest.xml"),
            File("src/debug/AndroidManifest.xml"),
        ).firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("debug AndroidManifest.xml not found from ${System.getProperty("user.dir")}")
        assertTrue(manifest.contains(".debug.LiquidBottomTabsFixtureActivity"))

        val fixture = debugSourceFile("debug/LiquidBottomTabsFixtureActivity.kt")
        assertTrue(fixture.contains("val backdrop = rememberLayerBackdrop()"))
        assertTrue(fixture.contains(".layerBackdrop(backdrop)"))
        assertTrue(fixture.contains("UpstreamLiquidBottomTabs"))
        assertTrue(fixture.contains("LiquidBottomTabs("))
        assertTrue(fixture.contains("B · Codex production glass"))
        assertTrue(fixture.contains("C · Integrated production switching"))
        assertTrue(fixture.contains("D · Press preview / commit on release"))
        assertTrue(fixture.contains("E · Chromatic aberration presets"))
        assertTrue(fixture.contains("ChromaticAberrationFixture(contentColor)"))
        assertTrue(fixture.contains("private enum class AberrationBackdropMode"))
        assertTrue(fixture.contains("AberrationBackdropMode.MULTICOLOR"))
        assertTrue(fixture.contains("AberrationBackdropMode.BLACK"))
        assertTrue(fixture.contains("AberrationBackdropMode.WHITE"))
        assertTrue(fixture.contains("var backdropMode by remember { mutableStateOf(AberrationBackdropMode.MULTICOLOR) }"))
        assertTrue(fixture.contains("val experimentBackdrop = rememberLayerBackdrop()"))
        assertTrue(fixture.contains("Modifier.fillMaxSize().layerBackdrop(experimentBackdrop)"))
        assertTrue(fixture.contains("FixtureBackdrop(showLabel = false)"))
        assertTrue(fixture.contains("Color.Black"))
        assertTrue(fixture.contains("Color.White"))
        assertTrue(fixture.contains("backdrop = experimentBackdrop"))
        assertTrue(fixture.contains("P0 · Legacy · 10 / 14"))
        assertTrue(fixture.contains("P1 · Production · 11 / 18"))
        assertTrue(fixture.contains("P2 · Strong · 12 / 20"))
        assertTrue(fixture.contains("P3 · Reference · 14 / 24"))
        assertTrue(fixture.contains("indicatorRefractionHeight = refractionHeight"))
        assertTrue(fixture.contains("indicatorRefractionAmount = refractionAmount"))
        assertTrue(fixture.contains("var selectedIndex by remember { mutableIntStateOf(0) }"))
        assertTrue(fixture.contains("Hold or drag each preset to reveal full chromatic aberration."))
        assertTrue(fixture.contains("Black/white isolate the glass edge; multicolor reveals RGB separation most clearly."))
        assertTrue(fixture.contains("PressPreviewFixture"))
        assertTrue(fixture.contains("Committed page:"))
        assertTrue(fixture.contains("committed index:"))
        assertTrue(fixture.contains("PRESS OTHER TAB · HOLD · RELEASE · CANCEL / MOVE AWAY"))
        assertTrue(fixture.contains("QUICK TAP → preview → commit on release"))
        assertTrue(fixture.contains("HOLD other tab · pill stays at target · stable press shape · page unchanged until release"))
        assertTrue(fixture.contains("CANCEL / MOVE AWAY · pill smoothly returns · no page switch"))
        assertTrue(fixture.contains("DRAG selected pill · original velocity stretch remains"))
        assertTrue(fixture.contains("tap/hold preview should not use drag velocity deformation"))
        assertTrue(fixture.contains("PRESS OTHER TAB → HOLD → DRAG ACROSS → RELEASE"))
        assertTrue(fixture.contains("HOLD + DRAG → preview hands off → no snap back → commit nearest tab on release"))
        assertTrue(fixture.contains("DIRECT DRAG SELECTED → original production highlight + drag behavior remains"))
        assertTrue(fixture.contains("Preview/hold highlight follows the pill's actual distance to the target; release still commits."))
        assertTrue(fixture.contains("Slow taps can expose the distance-based preview highlight tradeoff; no long-press detector is used."))
        assertTrue(fixture.contains("After drag handoff, local highlight transitions from the preview value and fades on release/cancel."))
        val pressPreview = fixture.substringAfter("private fun PressPreviewFixture")
            .substringBefore("private fun ProductionLiquidTabs")
        assertFalse(pressPreview.contains("long-press highlight"))
        assertFalse(pressPreview.contains("prewarmed"))
        assertFalse(pressPreview.contains("reveal"))
        assertFalse(pressPreview.contains("highlight follows pointer"))
        assertTrue(fixture.contains("drag after preview should use the same velocity deformation as selected-pill drag"))
        assertTrue(fixture.contains("AnimatedContent("))
        assertTrue(fixture.contains("fadeIn(animationSpec = tween(200))"))
        assertTrue(fixture.contains("initialOffsetX = { width -> direction * width / 20 }"))
        assertTrue(fixture.contains("fadeOut(animationSpec = tween(160))"))
        assertTrue(fixture.contains("targetOffsetX = { width -> -direction * width / 28 }"))
        assertTrue(fixture.contains("Auto stress ×100"))
        assertTrue(fixture.contains("repeat(100)"))
        assertTrue(fixture.contains("LiquidBottomTab(tabIndex = 0"))
        assertTrue(fixture.contains("LiquidBottomTab(tabIndex = 1"))

        val upstreamTabs = debugSourceFile("liquidglass/UpstreamLiquidBottomTabs.kt")
        assertTrue(upstreamTabs.contains("lerp(1f, 1.2f, dampedDragAnimation.pressProgress)"))
        assertTrue(upstreamTabs.contains("rememberCombinedBackdrop(backdrop, tabsBackdrop)"))
        assertTrue(upstreamTabs.contains("ColorFilter.tint(accentColor)"))
        assertTrue(upstreamTabs.contains("UpstreamDampedDragAnimation("))
        assertTrue(upstreamTabs.contains("UpstreamInteractiveHighlight("))
        assertFalse(upstreamTabs.contains("            DampedDragAnimation("))
        assertFalse(upstreamTabs.contains("            InteractiveHighlight("))
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

    @Test
    fun authPairingAndUpdateFixturesAreDebugOnlyAndSideEffectFree() {
        val settings = settingsSource()
        val developerOptions = settings.substringAfter("SettingsSection(\"开发者选项\")")
            .substringBefore("private fun openDebugQuotaRingFixture")
        listOf(
            "Codex Login Fixture",
            "Windows Pairing Fixture",
            "Update Download Fixture",
        ).forEach { title -> assertTrue(developerOptions.contains(title)) }
        listOf(
            "DEBUG_CODEX_LOGIN_FIXTURE_ACTIVITY",
            "DEBUG_WINDOWS_PAIRING_FIXTURE_ACTIVITY",
            "DEBUG_UPDATE_DOWNLOAD_FIXTURE_ACTIVITY",
            "openDebugCodexLoginFixture",
            "openDebugWindowsPairingFixture",
            "openDebugUpdateDownloadFixture",
        ).forEach { marker -> assertTrue(settings.contains(marker)) }

        val manifest = debugManifestSource()
        listOf(
            ".debug.CodexLoginFixtureActivity",
            ".debug.WindowsPairingFixtureActivity",
            ".debug.UpdateDownloadFixtureActivity",
        ).forEach { activity ->
            val entry = manifest.substringAfter(activity).substringBefore("</activity>")
            assertTrue(entry.contains("android:exported=\"false\""))
            assertFalse(entry.contains("intent-filter"))
        }

        val login = debugSourceFile("debug/CodexLoginFixtureActivity.kt")
        assertTrue(login.contains("正在准备登录…"))
        assertTrue(login.contains("请在浏览器完成 OpenAI 登录"))
        assertTrue(login.contains("ABCD-EFGH"))
        assertTrue(login.contains("登录完成，正在保存登录状态…"))
        assertTrue(login.contains("登录失败，请重试"))
        assertTrue(login.contains("localActionCount++"))
        assertFalse(login.contains("CodexOAuthClient"))
        assertFalse(login.contains("OAuthStore"))
        assertFalse(login.contains("Intent("))

        val pairing = debugSourceFile("debug/WindowsPairingFixtureActivity.kt")
        assertTrue(pairing.contains("电脑"))
        assertTrue(pairing.contains("Windows PC"))
        assertTrue(pairing.contains("192.168.1.58:43127"))
        assertTrue(pairing.contains("2 分钟前"))
        assertTrue(pairing.contains("重新扫码配对"))
        assertTrue(pairing.contains("复制诊断信息"))
        assertTrue(pairing.contains("解除配对"))
        assertTrue(pairing.contains("CodexConfirmDialog"))
        assertFalse(pairing.contains("TokenPairingFlow"))
        assertFalse(pairing.contains("TokenSyncStore"))

        val update = debugSourceFile("debug/UpdateDownloadFixtureActivity.kt")
        assertTrue(update.contains("UpdateAvailableDialog("))
        assertTrue(update.contains("DOWNLOADING_INDETERMINATE"))
        assertTrue(update.contains("DOWNLOADING_PROGRESS"))
        assertTrue(update.contains("UpdateDownloadPhase.VERIFYING"))
        assertTrue(update.contains("Fixture simulated failure"))
        assertTrue(update.contains("https://example.invalid"))
        assertFalse(update.contains("UpdateDownloadManager"))
        assertFalse(update.contains("UpdateInstaller"))
        assertFalse(update.contains("UpdateBrowser"))
    }

    @Test
    fun p1PageSurfacesUseLiquidActionsAndKeepDialogProgressTopology() {
        val quota = sourceFile("QuotaPageView.kt")
        val quotaContent = quota
            .substringAfter("internal fun QuotaPageContent(")
            .substringBefore("private fun QuotaStatusLine(")
        assertTrue(quota.contains("QuotaPageContent("))
        assertTrue(quotaContent.contains("DataSourceEmptyStateCard("))
        assertTrue(quotaContent.contains("message = \"登录 OpenAI 或连接 Windows CodexQuotaTray 后，即可查看 Codex 额度。\""))
        assertTrue(quotaContent.contains("onLoginOpenAi = onLogin"))
        assertTrue(quotaContent.contains("onPairWindows = onPairing"))
        assertTrue(quotaContent.contains("loginEnabled = !busy"))
        assertFalse(quota.contains("androidx.compose.material3.Button"))
        assertFalse(quotaContent.contains("verticalScroll"))

        val token = sourceFile("TokenUsagePageView.kt")
        val tokenContent = token
            .substringAfter("internal fun TokenUsagePageContent(")
            .substringBefore("private fun TokenUsageStatusLine(")
        assertTrue(token.contains("TokenUsagePageContent("))
        assertTrue(tokenContent.contains("DataSourceEmptyStateCard("))
        assertTrue(tokenContent.contains("message = \"登录 OpenAI 或连接 Windows CodexQuotaTray 后，即可查看 Token 使用历史。\""))
        assertTrue(tokenContent.contains("onLoginOpenAi = onLoginOpenAi"))
        assertTrue(tokenContent.contains("onPairWindows = onPairing"))
        assertFalse(token.contains("androidx.compose.material3.Button"))
        assertFalse(tokenContent.contains("verticalScroll"))

        val emptyState = sourceFile("DataSourceEmptyState.kt")
        assertTrue(emptyState.contains("RoundedCornerShape(14.dp)"))
        assertTrue(emptyState.contains("label = \"登录 OpenAI\""))
        assertTrue(emptyState.contains("primary = true"))
        assertTrue(emptyState.contains("label = \"扫码配对\""))
        assertTrue(emptyState.contains("onClick = onPairWindows"))
        assertTrue(emptyState.contains("Arrangement.spacedBy(8.dp)"))

        val liquidDialog = sourceFile("LiquidDialogSurface.kt")
        assertTrue(liquidDialog.contains("internal fun LiquidDialogSurface("))
        assertTrue(liquidDialog.contains("backdrop: Backdrop"))
        assertTrue(liquidDialog.contains("GlassSurface("))
        assertTrue(liquidDialog.contains("backdrop = backdrop"))
        assertTrue(liquidDialog.contains("RoundedCornerShape(SettingsUiTokens.groupCornerRadius)"))
        assertTrue(liquidDialog.contains("width = 1.dp"))
        assertTrue(liquidDialog.contains("blurRadius = 8.dp"))
        assertTrue(liquidDialog.contains("refractionHeight = 12.dp"))
        assertTrue(liquidDialog.contains("refractionAmount = 24.dp"))
        assertTrue(liquidDialog.contains("surfaceAlpha = if (isDark) 0.46f else 0.58f"))
        assertTrue(liquidDialog.contains(".padding(vertical = 20.dp)"))
        assertTrue(liquidDialog.contains("Arrangement.spacedBy(18.dp)"))
        assertFalse(liquidDialog.contains("rememberLayerBackdrop()"))
        assertFalse(liquidDialog.contains(".matchParentSize()"))
        assertFalse(liquidDialog.contains(".alpha(0f)"))
        assertFalse(liquidDialog.contains(".layerBackdrop("))
        assertFalse(liquidDialog.contains(".background("))

        val liquidModal = sourceFile("LiquidModalOverlay.kt")
        assertTrue(liquidModal.contains("internal fun LiquidModalOverlay("))
        assertFalse(liquidModal.contains("backdrop"))
        assertTrue(liquidModal.contains("BackHandler(enabled = true)"))
        assertTrue(liquidModal.contains("Color.Black.copy(alpha = 0.32f)"))
        assertTrue(liquidModal.contains(".fillMaxSize()"))
        assertTrue(liquidModal.contains("clickable("))
        assertTrue(liquidModal.contains("interactionSource = null"))
        assertTrue(liquidModal.contains("indication = null"))
        assertTrue(liquidModal.contains("clearAndSetSemantics { }"))
        assertTrue(liquidModal.contains("ModalScrimPointerBlocker("))
        assertTrue(liquidModal.contains("PointerEventPass.Final"))
        assertTrue(liquidModal.contains("event.changes"))
        assertTrue(liquidModal.contains("change.consume()"))
        assertTrue(liquidModal.contains("ModalSurfacePointerBarrier("))
        assertTrue(liquidModal.contains("dismissOnBackPress"))
        assertTrue(liquidModal.contains("dismissOnClickOutside"))
        assertTrue(liquidModal.contains("dialog()"))
        assertTrue(liquidModal.contains("dismiss(\"关闭\")"))
        assertTrue(liquidModal.contains("this.paneTitle = paneTitle"))
        assertFalse(liquidModal.contains("detectTapGestures"))
        assertFalse(liquidModal.contains("pointerIsDown"))
        assertFalse(liquidModal.contains("tapCandidate"))
        assertFalse(liquidModal.contains("changedToDownIgnoreConsumed"))
        assertFalse(liquidModal.contains("changedToUpIgnoreConsumed"))
        assertFalse(liquidModal.contains("shouldDismissModalScrimTap"))

        val update = sourceFile("UpdateUi.kt")
        assertTrue(update.contains("UpdateAvailableDialog("))
        assertTrue(update.contains("backdrop: Backdrop"))
        assertTrue(update.contains("LiquidModalOverlay("))
        assertTrue(update.contains("LiquidDialogSurface(backdrop = backdrop)"))
        assertTrue(update.contains("paneTitle = \"发现新版本\""))
        assertFalse(update.contains("semantics { paneTitle"))
        assertFalse(update.contains("internal fun LiquidDialogSurface("))
        assertFalse(update.contains("rememberLayerBackdrop()"))
        assertFalse(update.contains(".matchParentSize()"))
        assertFalse(update.contains("androidx.compose.ui.window.Dialog"))
        assertFalse(update.contains("DialogProperties("))
        assertTrue(update.contains("private fun UpdateDownloadProgressBar("))
        assertTrue(update.contains("internal fun updateDownloadProgressFraction("))
        assertTrue(update.contains("internal fun progressCornerRadiusPx("))
        assertTrue(update.contains("internal fun updateDownloadProgressVisualWidth("))
        assertTrue(update.contains("ProgressBarRangeInfo(fraction, 0f..1f)"))
        assertTrue(update.contains(".height(8.dp)"))
        assertTrue(update.contains("val radius = progressCornerRadiusPx("))
        assertTrue(Regex("drawRoundRect\\(").findAll(update).count() >= 2)
        assertTrue(update.contains("val visualProgressWidth = updateDownloadProgressVisualWidth("))
        assertTrue(update.contains("size = Size(visualProgressWidth, size.height)"))
        assertTrue(update.contains("CornerRadius(radius, radius)"))
        assertFalse(update.contains("Path"))
        assertFalse(update.contains("LinearProgressIndicator"))
        assertFalse(update.contains("SettingsSection(\"下载\")"))
        assertTrue(update.contains("Modifier.size(20.dp)"))
        assertFalse(update.contains("Card("))

        val codexUi = sourceFile("CodexUi.kt")
        assertTrue(codexUi.contains("internal fun CodexConfirmDialog("))
        assertTrue(codexUi.contains("backdrop: Backdrop"))
        assertTrue(codexUi.contains("LiquidModalOverlay("))
        assertTrue(codexUi.contains("backdrop = backdrop"))
        assertTrue(codexUi.contains("LiquidDialogSurface("))
        assertFalse(codexUi.contains("semantics { paneTitle = title }"))
        assertTrue(codexUi.contains("TextButton(onClick = hapticDismiss)"))
        assertTrue(codexUi.contains("TextButton(onClick = hapticConfirm)"))
        assertTrue(codexUi.contains("onConfirm(); onDismiss()"))
        assertTrue(codexUi.contains("CodexColors.danger"))
        assertTrue(codexUi.contains("modalContent: @Composable BoxScope.(Backdrop) -> Unit = {}"))
        assertTrue(codexUi.contains("modalContent(backdrop)"))
        assertFalse(codexUi.contains("androidx.compose.ui.window.Dialog"))
        assertFalse(codexUi.contains("DialogProperties("))
        assertFalse(codexUi.contains("AlertDialog"))
        assertFalse(codexUi.contains("LiquidActionButton"))

        listOf(
            "AccountActivity.kt" to "退出登录",
            "SettingsActivity.kt" to "解除配对",
            "LogActivity.kt" to "清空日志",
        ).forEach { (file, title) ->
            val activity = sourceFile(file)
            assertTrue(activity.contains("CodexConfirmDialog("))
            assertTrue(activity.contains("title = \"$title\""))
        }
        assertTrue(sourceFile("AccountActivity.kt").contains("modalContent = { backdrop ->"))
        assertTrue(sourceFile("LogActivity.kt").contains("modalContent = { backdrop ->"))
        assertTrue(sourceFile("SettingsActivity.kt").contains("backdrop = pageBackdrop"))
        assertTrue(sourceFile("MainActivity.kt").contains("backdrop = chromeBackdrop"))
        assertTrue(debugSourceFile("debug/WindowsPairingFixtureActivity.kt").contains("modalContent = { backdrop ->"))
        assertTrue(debugSourceFile("debug/UpdateDownloadFixtureActivity.kt").contains("modalContent = { backdrop ->"))
    }

    @Test
    fun sourceEmptyStateCallbacksAndAccountNamingUseExistingFlows() {
        val main = sourceFile("MainActivity.kt")
        assertTrue(main.contains("QuotaPage(quota, ::scanTokenPairing)"))
        assertTrue(main.contains("TokenUsagePage(usage, ::scanTokenPairing, quota::openLogin)"))

        val settings = settingsSource()
        assertTrue(settings.contains("title = \"OpenAI 账号\""))
        assertFalse(settings.contains("title = \"Codex 额度账号\""))

        val account = sourceFile("AccountActivity.kt")
        assertTrue(account.contains("title = \"OpenAI 账号\""))
        assertTrue(account.contains("尚未登录 OpenAI"))
        assertTrue(account.contains("label = if (credentials == null) \"登录 OpenAI\""))
        assertFalse(account.contains("Codex 额度账号"))
        assertFalse(account.contains("尚未登录 Codex"))
        assertFalse(account.contains("登录 Codex"))

        val login = sourceFile("LoginActivity.kt")
        assertTrue(login.contains("title = \"登录 OpenAI\""))
        assertTrue(login.contains("请在浏览器完成 OpenAI 登录"))
        assertFalse(login.contains("title = \"登录 Codex\""))
        assertFalse(login.contains("请在浏览器完成 Codex 登录"))

        val fixture = debugSourceFile("debug/CodexLoginFixtureActivity.kt")
        assertTrue(fixture.contains("title = \"登录 OpenAI\""))
        assertTrue(fixture.contains("请在浏览器完成 OpenAI 登录"))
    }

    @Test
    fun dashboardUsesPageTitlesAndHierarchicalSummaryContent() {
        val main = sourceFile("MainActivity.kt")
        assertTrue(main.contains("text = if (selectedIndex == 0) \"额度\" else \"统计\""))
        assertFalse(main.contains("Text(\"CodexQuota\""))

        val quota = sourceFile("QuotaPageView.kt")
        val quotaContent = quota
            .substringAfter("internal fun QuotaPageContent(")
            .substringBefore("private fun QuotaStatusLine(")
        assertFalse(quotaContent.contains("Text(\n            \"额度\""))
        val quotaWindow = quota.substringAfter("private fun QuotaWindowCard(")
            .substringBefore("private fun ResetCreditCard(")
        assertOrdered(
            quotaWindow,
            "window.title",
            "formatRemaining(window.resetsAt)",
            "formatResetAt(window.resetsAt, locale)",
        )
        assertTrue(quotaWindow.contains("fontSize = 15.sp"))
        assertTrue(quotaWindow.contains("fontWeight = FontWeight.Medium"))
        assertTrue(quotaWindow.contains("palette.color(palette.body)"))
        assertTrue(quotaWindow.contains("fontSize = 13.sp"))
        assertTrue(quotaWindow.contains("palette.color(palette.muted)"))
        assertTrue(quota.contains("return \"\$absolute 重置\""))

        val token = sourceFile("TokenUsagePageView.kt")
        val tokenContent = token
            .substringAfter("internal fun TokenUsagePageContent(")
            .substringBefore("private fun TokenUsageStatusLine(")
        assertFalse(tokenContent.contains("Text(\"统计\""))
        assertTrue(token.contains("TokenSummaryCard(presentation)"))
        val summaryCard = token.substringAfter("private fun TokenSummaryCard(")
            .substringBefore("private fun TokenSummaryDivider(")
        assertTrue(summaryCard.contains("DashboardCardSurface"))
        assertTrue(summaryCard.contains("Column(Modifier.fillMaxWidth())"))
        assertTrue(summaryCard.contains("presentation.first"))
        assertTrue(summaryCard.contains("presentation.second"))
        assertFalse(summaryCard.contains("\"Token 分类\""))
        assertTrue(summaryCard.contains("presentation.categories"))
        assertEquals(1, summaryCard.split("TokenSummaryDivider()").size - 1)
        assertFalse(summaryCard.contains("valueSize = 15.sp"))
        assertFalse(summaryCard.contains("valueSize = 14.sp"))
        assertFalse(summaryCard.contains("valueWeight = FontWeight.Medium"))
        assertFalse(summaryCard.contains("labelSize = 10.sp"))
        assertFalse(summaryCard.contains("itemVerticalPadding = 7.dp"))
        assertFalse(summaryCard.contains("itemVerticalPadding = 6.dp"))
        assertFalse(summaryCard.contains(".background("))
        assertFalse(summaryCard.contains(".border("))
        assertFalse(summaryCard.contains("RoundedCornerShape("))
        assertFalse(summaryCard.contains("rememberLayerBackdrop"))
        assertFalse(summaryCard.contains("GlassSurface"))
        val metricRow = token.substringAfter("private fun TokenMetricRow(")
            .substringBefore("internal fun tokenSummaryValueLabel")
        assertTrue(metricRow.contains("maxLines = 1"))
        assertTrue(metricRow.contains("overflow = TextOverflow.Ellipsis"))
        listOf("今日 Token", "7 天 Token", "30 天 Token", "累计 Token", "峰值 Token", "当前连续", "最长连续").forEach {
            assertTrue(token.contains("\"$it\""))
        }
        listOf("输入", "缓存输入", "输出", "推理").forEach {
            assertTrue(token.contains("\"$it\""))
        }
        assertTrue(token.contains("shouldShowTokenCategories(presentation.categories)"))
    }

    @Test
    fun dashboardCardsShareTheQuotaSurfaceAndResetCreditHierarchy() {
        val surface = sourceFile("DashboardCardSurface.kt")
        assertTrue(surface.contains("internal fun DashboardCardSurface("))
        assertTrue(surface.contains("RoundedCornerShape(18.dp)"))
        assertTrue(surface.contains("Brush.linearGradient"))
        assertTrue(surface.contains("Brush.sweepGradient"))
        assertTrue(surface.contains(".border(1.dp, borderBrush, cardShape)"))
        assertTrue(surface.contains(".padding(16.dp)"))

        val quota = sourceFile("QuotaPageView.kt")
        assertFalse(quota.contains("QuotaCardSurface"))
        val quotaWindow = quota.substringAfter("private fun QuotaWindowCard(")
            .substringBefore("private fun ResetCreditCard(")
        assertTrue(quotaWindow.contains("DashboardCardSurface"))
        val resetCard = quota.substringAfter("private fun ResetCreditCard(")
            .substringBefore("private fun ResetCreditRow(")
        assertTrue(resetCard.contains("DashboardCardSurface"))
        val resetRow = quota.substringAfter("private fun ResetCreditRow(")
            .substringBefore("@Composable\ninternal fun QuotaProgressRing(")
        assertOrdered(
            resetRow,
            "R.string.reset_credit_remaining",
            "R.string.reset_credit_expiry",
        )
        assertTrue(resetRow.contains("fontSize = 15.sp"))
        assertTrue(resetRow.contains("fontWeight = FontWeight.Medium"))
        assertTrue(resetRow.contains("palette.color(palette.body)"))
        assertTrue(resetRow.contains("fontSize = 13.sp"))
        assertTrue(resetRow.contains("fontWeight = FontWeight.Normal"))
        assertTrue(resetRow.contains("palette.color(palette.muted)"))

        val token = sourceFile("TokenUsagePageView.kt")
        assertFalse(token.contains("QuotaCardSurface"))
        assertTrue(token.contains("DashboardCardSurface"))
        val resources = resourceFile("values/strings.xml")
        assertTrue(resources.contains("<string name=\"reset_credit_expiry\">%1\$s 到期</string>"))
    }

    @Test
    fun updateFixtureExposesContinuousProgressRegressionValues() {
        val fixture = debugSourceFile("debug/UpdateDownloadFixtureActivity.kt")
        listOf(
            "DOWNLOADING_PROGRESS_0",
            "DOWNLOADING_PROGRESS_1",
            "DOWNLOADING_PROGRESS",
            "DOWNLOADING_PROGRESS_99",
            "DOWNLOADING_PROGRESS_100",
            "\"0%\"",
            "\"1%\"",
            "\"42%\"",
            "\"99%\"",
            "\"100%\"",
            "FIXTURE_TOTAL_BYTES",
        ).forEach { marker -> assertTrue(fixture.contains(marker)) }
        assertFalse(fixture.contains("UpdateDownloadManager"))
        assertFalse(fixture.contains("UpdateInstaller"))
    }

    @Test
    fun updateProgressFractionClampsToContinuousBarRange() {
        assertEquals(0f, updateDownloadProgressFraction(-0.5f), 0f)
        assertEquals(0f, updateDownloadProgressFraction(0f), 0f)
        assertEquals(0.01f, updateDownloadProgressFraction(0.01f), 0f)
        assertEquals(0.42f, updateDownloadProgressFraction(0.42f), 0f)
        assertEquals(0.99f, updateDownloadProgressFraction(0.99f), 0f)
        assertEquals(1f, updateDownloadProgressFraction(1f), 0f)
        assertEquals(1f, updateDownloadProgressFraction(1.5f), 0f)
    }

    @Test
    fun progressCornerRadiusUsesTheBarCapsuleRadius() {
        assertEquals(4f, progressCornerRadiusPx(8f, 4f), 0f)
        assertEquals(3f, progressCornerRadiusPx(6f, 4f), 0f)
        assertEquals(0f, progressCornerRadiusPx(-2f, 4f), 0f)
    }

    @Test
    fun progressVisualWidthKeepsTinyValuesVisibleWithoutChangingFraction() {
        assertEquals(0f, updateDownloadProgressVisualWidth(0f, 100f, 8f), 0f)
        assertEquals(8f, updateDownloadProgressVisualWidth(0.01f, 100f, 8f), 0f)
        assertEquals(42f, updateDownloadProgressVisualWidth(0.42f, 100f, 8f), 0f)
        assertEquals(99f, updateDownloadProgressVisualWidth(0.99f, 100f, 8f), 0f)
        assertEquals(100f, updateDownloadProgressVisualWidth(1f, 100f, 8f), 0f)
    }

    @Test
    fun quotaAndTokenPageFixturesAreDebugOnlyFakeAndReachable() {
        val settings = settingsSource()
        val developerOptions = settings.substringAfter("SettingsSection(\"开发者选项\")")
            .substringBefore("private fun openDebugQuotaRingFixture")
        listOf(
            "Quota Page Fixture",
            "Token Usage Page Fixture",
            "DEBUG_QUOTA_PAGE_FIXTURE_ACTIVITY",
            "DEBUG_TOKEN_USAGE_PAGE_FIXTURE_ACTIVITY",
            "openDebugQuotaPageFixture",
            "openDebugTokenUsagePageFixture",
        ).forEach { marker -> assertTrue(settings.contains(marker)) }
        assertTrue(developerOptions.contains("Quota Page Fixture"))
        assertTrue(developerOptions.contains("Token Usage Page Fixture"))

        val manifest = debugManifestSource()
        listOf(
            ".debug.QuotaPageFixtureActivity",
            ".debug.TokenUsagePageFixtureActivity",
        ).forEach { activity ->
            val entry = manifest.substringAfter(activity).substringBefore("</activity>")
            assertTrue(entry.contains("android:configChanges=\"uiMode\""))
            assertTrue(entry.contains("android:exported=\"false\""))
            assertTrue(entry.contains("android:screenOrientation=\"portrait\""))
            assertFalse(entry.contains("intent-filter"))
        }

        val quota = debugSourceFile("debug/QuotaPageFixtureActivity.kt")
        listOf(
            "UNAUTHENTICATED",
            "LOADING_NO_CACHE",
            "LOADED_SINGLE",
            "LOADED_DUAL",
            "RESET_CREDITS",
            "ERROR_WITH_CACHE",
            "EMPTY_LOADED",
            "QuotaPageContent(",
            "quotaLoadingUiModel()",
            "quotaErrorUiModel(",
            "localActionCount",
        ).forEach { marker -> assertTrue(quota.contains(marker)) }
        assertFalse(quota.contains("QuotaPageController"))
        assertFalse(quota.contains("CodexQuotaRepository"))
        assertFalse(quota.contains("OAuthStore"))
        assertFalse(quota.contains("WorkManager"))

        val token = debugSourceFile("debug/TokenUsagePageFixtureActivity.kt")
        listOf(
            "UNPAIRED",
            "LOADED_TYPICAL",
            "SPARSE_HISTORY",
            "LARGE_NUMBERS",
            "MISSING_CATEGORY_BREAKDOWN",
            "ERROR_STALE_WITH_SNAPSHOT",
            "TokenUsagePageContent(",
            "TokenUsageSnapshot(",
            "localActionCount",
        ).forEach { marker -> assertTrue(token.contains(marker)) }
        assertFalse(token.contains("TokenUsagePageController"))
        assertFalse(token.contains("TokenSyncStore"))
        assertFalse(token.contains("TokenUsageCache"))
        assertFalse(token.contains("TokenUsageSyncCoordinator"))
        assertFalse(token.contains("WorkManager"))
    }

    @Test
    fun liquidTokenTooltipFixtureKeepsProductionInteractionAndComparesThreeSurfaces() {
        val settings = settingsSource()
        val developerOptions = settings.substringAfter("SettingsSection(\"开发者选项\")")
            .substringBefore("private fun openDebugQuotaRingFixture")
        assertTrue(developerOptions.contains("Liquid Token Tooltip Fixture"))
        assertTrue(settings.contains("DEBUG_LIQUID_TOKEN_TOOLTIP_FIXTURE_ACTIVITY"))
        assertTrue(settings.contains("openDebugLiquidTokenTooltipFixture"))

        val manifest = debugManifestSource()
        val manifestEntry = manifest
            .substringAfter(".debug.LiquidTokenTooltipFixtureActivity")
            .substringBefore("</activity>")
        assertTrue(manifestEntry.contains("android:exported=\"false\""))
        assertFalse(manifestEntry.contains("intent-filter"))

        val fixture = debugSourceFile("debug/LiquidTokenTooltipFixtureActivity.kt")
        assertTrue(fixture.contains("class LiquidTokenTooltipFixtureActivity"))
        assertEquals(1, Regex("rememberLayerBackdrop\\(\\)").findAll(fixture).count())
        assertTrue(fixture.contains(".layerBackdrop(backdrop)"))
        assertTrue(fixture.contains(".background(pageBackground)"))
        assertTrue(fixture.contains(".hazeSource(hazeState)"))
        assertTrue(fixture.contains("heatmapOriginInRoot"))
        assertTrue(fixture.contains("coordinates.positionInRoot()"))
        assertTrue(fixture.contains("selectedBoundsInRoot"))
        assertFalse(fixture.contains("positionInParent()"))
        assertTrue(fixture.contains("TOKEN_HEATMAP_COLUMNS * TOKEN_HEATMAP_ROWS"))
        assertTrue(fixture.contains("detectTokenHeatmapGestures"))
        assertTrue(fixture.contains("heatmapGestureOnDown"))
        assertTrue(fixture.contains("heatmapGestureOnMove"))
        assertTrue(fixture.contains("heatmapGestureShouldClear"))
        assertTrue(fixture.contains("placeHeatmapTooltip"))
        assertTrue(fixture.contains("FixtureTooltipStyle.CURRENT"))
        assertTrue(fixture.contains("FixtureTooltipStyle.DIALOG"))
        assertTrue(fixture.contains("FixtureTooltipStyle.MAGNIFIER"))
        assertTrue(fixture.contains("rememberFixtureTooltipOffset(tooltipPresentation?.target)"))
        assertTrue(fixture.contains("positionAnimation.snapTo(target)"))
        assertTrue(fixture.contains("dampingRatio = 0.5f"))
        assertTrue(fixture.contains("stiffness = 300f"))
        assertTrue(fixture.contains("appearanceScale.animateTo(1f, tween(160))"))
        assertTrue(fixture.contains("liquidTokenDialogSurfaceModifier"))
        assertTrue(fixture.contains("lens(\n                    8.dp.toPx(),\n                    24.dp.toPx(),"))
        assertTrue(fixture.contains("chromaticAberration = true"))
        assertTrue(fixture.contains("InnerShadow(radius = 16.dp)"))
        assertFalse(fixture.contains("scale(1.5f, 1.5f)"))
        assertFalse(fixture.contains("translate(top ="))

        val dialogSurface = sourceFile("LiquidTokenTooltipSurface.kt")
        assertTrue(dialogSurface.contains("Color(0xFF121212).copy(alpha = 0.4f)"))
        assertTrue(dialogSurface.contains("Color(0xFFFAFAFA).copy(alpha = 0.6f)"))
        assertTrue(dialogSurface.contains("brightness = if (isDark) 0f else 0.2f"))
        assertTrue(dialogSurface.contains("saturation = 1.5f"))
        assertTrue(dialogSurface.contains("contrast = 1f"))
        assertTrue(dialogSurface.contains("blur(if (isDark) 8.dp.toPx() else 16.dp.toPx())"))
        assertTrue(dialogSurface.contains("lens(24.dp.toPx(), 48.dp.toPx(), depthEffect = true)"))
        assertTrue(dialogSurface.contains("Highlight.Plain"))
        assertTrue(dialogSurface.contains("onDrawSurface = { drawRect(containerColor) }"))

        val production = sourceFile("TokenUsagePageView.kt")
        val interaction = sourceFile("TokenHeatmapInteraction.kt")
        assertTrue(production.contains("val tokenContentBackdrop = rememberLayerBackdrop()"))
        assertTrue(production.contains(".layerBackdrop(tokenContentBackdrop)"))
        assertTrue(production.contains(".background(palette.color(palette.background))"))
        assertTrue(production.contains("HeatmapLiquidTooltip("))
        assertFalse(production.contains("HeatmapBlurTooltip"))
        assertFalse(production.contains("tokenContentHazeState"))
        assertFalse(production.contains("hazeSource("))
        assertFalse(production.contains("hazeEffect("))
        assertTrue(interaction.contains("detectTokenHeatmapGestures"))
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

    private fun File.readNormalizedText(): String =
        readText()
            .replace("\r\n", "\n")
            .replace('\r', '\n')

    private fun sourceFile(name: String): String {
        val relative = "com/codexquotatray/android/$name"
        val candidates = listOf(
            File("android/app/src/main/java/$relative"),
            File("app/src/main/java/$relative"),
            File("src/main/java/$relative"),
        )
        return candidates.firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("$name source not found from ${System.getProperty("user.dir")}")
    }

    private fun resourceFile(relative: String): String {
        val candidates = listOf(
            File("android/app/src/main/res/$relative"),
            File("app/src/main/res/$relative"),
            File("src/main/res/$relative"),
        )
        return candidates.firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("resource not found: $relative")
    }

    private fun debugSourceFile(name: String): String {
        val relative = "com/codexquotatray/android/$name"
        val candidates = listOf(
            File("android/app/src/debug/java/$relative"),
            File("app/src/debug/java/$relative"),
            File("src/debug/java/$relative"),
        )
        return candidates.firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("$name debug source not found from ${System.getProperty("user.dir")}")
    }

    private fun debugManifestSource(): String {
        val candidates = listOf(
            File("android/app/src/debug/AndroidManifest.xml"),
            File("app/src/debug/AndroidManifest.xml"),
            File("src/debug/AndroidManifest.xml"),
        )
        return candidates.firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("debug AndroidManifest.xml not found from ${System.getProperty("user.dir")}")
    }
}
