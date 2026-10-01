package com.codexquotatray.android

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.drawscope.ContentDrawScope
import androidx.compose.ui.graphics.layer.drawLayer
import androidx.compose.ui.graphics.rememberGraphicsLayer
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

/** Shared by the dashboard and its offline Debug preview. Chrome stays outside the scroll layer. */
@Composable
internal fun DashboardScaffold(
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    onSettings: () -> Unit,
    actionEnabled: Boolean,
    actionBusy: Boolean,
    onAction: () -> Unit,
    modifier: Modifier = Modifier,
    modalContent: @Composable BoxScope.(Backdrop) -> Unit = {},
    content: @Composable (Int) -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val density = LocalDensity.current
    val navigationInset = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
    var headerHeight by remember { mutableStateOf(64.dp) }
    var dockHeight by remember { mutableStateOf(88.dp + navigationInset) }
    val quotaScrollState = rememberScrollState()
    val tokenScrollState = rememberScrollState()
    var quotaUpwardOverscroll by remember { mutableStateOf(false) }
    var tokenUpwardOverscroll by remember { mutableStateOf(false) }
    val activeScrollState = if (selectedIndex == 0) quotaScrollState else tokenScrollState
    val isScrolled by remember(activeScrollState) {
        derivedStateOf { activeScrollState.value > 0 }
    }
    val upwardOverscroll = if (selectedIndex == 0) quotaUpwardOverscroll else tokenUpwardOverscroll
    val sceneLayer = rememberGraphicsLayer()
    val drawSceneLayer: ContentDrawScope.() -> Unit = remember(sceneLayer) {
        { drawLayer(sceneLayer) }
    }
    val chromeBackdrop = rememberLayerBackdrop(onDraw = drawSceneLayer)

    Box(modifier.fillMaxSize()) {
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(chromeBackdrop)
                .drawWithContent {
                    val content = this
                    sceneLayer.record { content.drawContent() }
                    drawLayer(sceneLayer)
                }
                .background(palette.color(palette.background)),
        ) {
            AnimatedContent(
                targetState = selectedIndex,
                modifier = Modifier.fillMaxSize(),
                transitionSpec = {
                    val direction = if (targetState > initialState) 1 else -1
                    (
                        fadeIn(animationSpec = tween(200)) +
                            slideInHorizontally(
                                animationSpec = tween(200),
                                initialOffsetX = { width -> direction * width / 20 },
                            )
                        ) togetherWith (
                        fadeOut(animationSpec = tween(160)) +
                            slideOutHorizontally(
                                animationSpec = tween(160),
                                targetOffsetX = { width -> -direction * width / 28 },
                            )
                        )
                },
                label = "main-page-transition",
            ) { pageIndex ->
                DashboardScrollContent(
                    scrollState = if (pageIndex == 0) quotaScrollState else tokenScrollState,
                    headerHeight = headerHeight,
                    dockHeight = dockHeight,
                    onUpwardOverscrollChanged = {
                        if (pageIndex == 0) quotaUpwardOverscroll = it else tokenUpwardOverscroll = it
                    },
                ) {
                    content(pageIndex)
                }
            }
        }
        SettingsGradientBlurHeader(
            backdrop = chromeBackdrop,
            scrollState = activeScrollState,
            isScrolled = isScrolled || upwardOverscroll,
            tint = palette.color(palette.background),
            modifier = Modifier.align(Alignment.TopCenter),
        )
        Box(Modifier.align(Alignment.TopCenter).fillMaxWidth().statusBarsPadding()) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .onSizeChanged { headerHeight = with(density) { it.height.toDp() } }
                    .heightIn(min = 56.dp)
                    .padding(start = 20.dp, end = 72.dp, top = 14.dp, bottom = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = if (selectedIndex == 0) "额度" else "统计",
                    color = palette.color(palette.title),
                    fontSize = 28.sp,
                    fontWeight = FontWeight.Bold,
                )
            }
            Box(Modifier.align(Alignment.TopEnd).padding(top = 8.dp, end = 20.dp)) {
                LiquidIconButton(
                    iconRes = R.drawable.ic_settings,
                    description = "设置",
                    backdrop = chromeBackdrop,
                    buttonSize = 48.dp,
                    iconSize = 24.dp,
                    onClick = onSettings,
                )
            }
        }
        LiquidMainDock(
            selectedIndex = selectedIndex,
            onSelected = onSelected,
            backdrop = chromeBackdrop,
            actionEnabled = actionEnabled,
            actionBusy = actionBusy,
            actionDescription = if (selectedIndex == 0) "刷新额度" else "同步统计",
            onAction = onAction,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .onSizeChanged { dockHeight = with(density) { it.height.toDp() } }
                .navigationBarsPadding()
                .padding(horizontal = 18.dp, vertical = 12.dp)
                .fillMaxWidth(),
        )
        modalContent(chromeBackdrop)
    }
}

@Composable
private fun DashboardScrollContent(
    scrollState: ScrollState,
    headerHeight: Dp,
    dockHeight: Dp,
    onUpwardOverscrollChanged: (Boolean) -> Unit,
    content: @Composable () -> Unit,
) {
    DisposableEffect(Unit) {
        onDispose { onUpwardOverscrollChanged(false) }
    }
    Box(
        Modifier
            .fillMaxSize()
            .dampedVerticalOverscroll { onUpwardOverscrollChanged(it < 0f) }
            .verticalScroll(scrollState, overscrollEffect = null)
            .statusBarsPadding()
            .padding(top = headerHeight, bottom = dockHeight + 12.dp),
    ) {
        content()
    }
}
