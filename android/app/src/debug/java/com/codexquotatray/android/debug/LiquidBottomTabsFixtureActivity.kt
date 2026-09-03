package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.R
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.ThemePalette
import com.codexquotatray.android.color
import com.codexquotatray.android.liquidglass.LiquidBottomTab
import com.codexquotatray.android.liquidglass.LiquidBottomTabs
import com.codexquotatray.android.liquidglass.UpstreamLiquidBottomTab
import com.codexquotatray.android.liquidglass.UpstreamLiquidBottomTabs
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class LiquidBottomTabsFixtureActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        val selectedTheme = AppTheme.mode(this)
        setTheme(
            if (AppTheme.effectiveMode(this, selectedTheme) == ThemeMode.DARK) {
                android.R.style.Theme_Material_NoActionBar
            } else {
                android.R.style.Theme_Material_Light_NoActionBar
            },
        )
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        val palette = AppTheme.palette(this, selectedTheme)
        setContent { CodexQuotaTheme(palette) { LiquidBottomTabsFixtureScreen(palette) } }
    }
}

@Composable
private fun LiquidBottomTabsFixtureScreen(palette: ThemePalette) {
    val backdrop = rememberLayerBackdrop()
    var upstreamSelected by remember { mutableIntStateOf(0) }
    var productionSelected by remember { mutableIntStateOf(0) }
    val upstreamState = rememberUpdatedState(upstreamSelected)
    val contentColor = palette.color(palette.body)

    Box(Modifier.fillMaxSize().background(palette.color(palette.background))) {
        Box(Modifier.fillMaxSize().layerBackdrop(backdrop)) { FixtureBackdrop() }
        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 18.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(24.dp),
        ) {
            Text("Liquid Bottom Tabs Fixture", color = Color.White, style = MaterialTheme.typography.headlineSmall)
            Text(
                "Same colorful backdrop · tap, hold, slow drag, fast drag",
                color = Color.White.copy(alpha = 0.7f),
                style = MaterialTheme.typography.bodyMedium,
            )
            FixtureSection("A · Kyant pinned upstream", upstreamSelected) {
                UpstreamLiquidBottomTabs(
                    selectedTabIndex = { upstreamState.value },
                    onTabSelected = { upstreamSelected = it },
                    backdrop = backdrop,
                    tabsCount = 2,
                    modifier = fixtureDockModifier(),
                ) {
                    UpstreamLiquidBottomTab(onClick = { upstreamSelected = 0 }) {
                        FixtureTabContent(R.drawable.ic_quota_tray, "额度", contentColor, 22, 24)
                    }
                    UpstreamLiquidBottomTab(onClick = { upstreamSelected = 1 }) {
                        FixtureTabContent(R.drawable.ic_usage, "统计", contentColor)
                    }
                }
            }
            FixtureSection("B · Codex production glass", productionSelected) {
                ProductionLiquidTabs(
                    selectedIndex = productionSelected,
                    onSelected = { productionSelected = it },
                    backdrop = backdrop,
                    contentColor = contentColor,
                    modifier = fixtureDockModifier(),
                )
            }
            IntegratedTransitionFixture(backdrop, contentColor)
            PressPreviewFixture(backdrop, contentColor)
            Text(
                "A/B/C/D share one backdrop; no OAuth, LAN, API, worker, or network access.",
                color = Color.White.copy(alpha = 0.7f),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun IntegratedTransitionFixture(backdrop: Backdrop, contentColor: Color) {
    var selectedIndex by remember { mutableIntStateOf(0) }
    var expectedIndex by remember { mutableIntStateOf(0) }
    var stressJob by remember { mutableStateOf<Job?>(null) }
    val scope = rememberCoroutineScope()
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("C · Integrated production switching", color = Color.White, style = MaterialTheme.typography.titleMedium)
        Box(
            Modifier.fillMaxWidth().height(132.dp)
                .background(Color.Black.copy(alpha = 0.22f), RoundedCornerShape(20.dp)),
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
                label = "fixture-main-page-transition",
            ) { pageIndex ->
                FakePageCard(pageIndex, Modifier.fillMaxSize())
            }
        }
        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            ProductionLiquidTabs(
                selectedIndex = selectedIndex,
                onSelected = { expectedIndex = it; selectedIndex = it },
                backdrop = backdrop,
                contentColor = contentColor,
                modifier = fixtureDockModifier(),
            )
        }
        Button(
            onClick = {
                stressJob?.cancel()
                stressJob = scope.launch {
                    repeat(100) { iteration ->
                        val target = (iteration + 1) % 2
                        expectedIndex = target
                        selectedIndex = target
                        delay(70)
                    }
                }
            },
        ) { Text("Auto stress ×100") }
        Text(
            "expected selected index: $expectedIndex · actual selected index: $selectedIndex",
            color = Color.White.copy(alpha = 0.78f),
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun PressPreviewFixture(backdrop: Backdrop, contentColor: Color) {
    var committedIndex by remember { mutableIntStateOf(0) }
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("D · Press preview / commit on release", color = Color.White, style = MaterialTheme.typography.titleMedium)
        Box(
            Modifier.fillMaxWidth().height(132.dp)
                .background(Color.Black.copy(alpha = 0.22f), RoundedCornerShape(20.dp)),
        ) {
            AnimatedContent(
                targetState = committedIndex,
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
                label = "fixture-press-preview-page-transition",
            ) { pageIndex ->
                Box(
                    Modifier.fillMaxSize().padding(14.dp).background(
                        if (pageIndex == 0) Color(0xFF176B87).copy(alpha = 0.72f)
                        else Color(0xFF69359C).copy(alpha = 0.72f),
                        RoundedCornerShape(16.dp),
                    ),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        "Committed page: ${if (pageIndex == 0) "额度" else "统计"}",
                        color = Color.White,
                    )
                }
            }
        }
        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            ProductionLiquidTabs(
                selectedIndex = committedIndex,
                onSelected = { committedIndex = it },
                backdrop = backdrop,
                contentColor = contentColor,
                modifier = fixtureDockModifier(),
            )
        }
        Text(
            "committed index: $committedIndex · page: ${if (committedIndex == 0) "额度" else "统计"}",
            color = Color.White.copy(alpha = 0.78f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "PRESS OTHER TAB · HOLD · RELEASE · CANCEL / MOVE AWAY",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "TAP other tab · pill smooth slide · no extreme stretch · page commits on release",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "HOLD other tab · pill stays at target · stable press shape · page unchanged until release",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "CANCEL / MOVE AWAY · pill smoothly returns · no page switch",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "DRAG selected pill · original velocity stretch remains",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "tap/hold preview should not use drag velocity deformation",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "PRESS OTHER TAB → HOLD → DRAG ACROSS → RELEASE",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "preview should hand off into drag instead of reverting on clickable cancel",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
        Text(
            "drag after preview should use the same velocity deformation as selected-pill drag",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun ProductionLiquidTabs(
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: Backdrop,
    contentColor: Color,
    modifier: Modifier,
) {
    val selectedState = rememberUpdatedState(selectedIndex)
    LiquidBottomTabs(
        selectedTabIndex = { selectedState.value },
        onTabSelected = onSelected,
        backdrop = backdrop,
        tabsCount = 2,
        modifier = modifier,
    ) {
        LiquidBottomTab(tabIndex = 0, onClick = { onSelected(0) }) {
            FixtureTabContent(R.drawable.ic_quota_tray, "额度", contentColor, 22, 24)
        }
        LiquidBottomTab(tabIndex = 1, onClick = { onSelected(1) }) {
            FixtureTabContent(R.drawable.ic_usage, "统计", contentColor)
        }
    }
}

@Composable
private fun FakePageCard(index: Int, modifier: Modifier) {
    Box(
        modifier.padding(14.dp).background(
            if (index == 0) Color(0xFF176B87).copy(alpha = 0.72f)
            else Color(0xFF69359C).copy(alpha = 0.72f),
            RoundedCornerShape(16.dp),
        ),
        contentAlignment = Alignment.Center,
    ) {
        Text(if (index == 0) "Fake Quota page" else "Fake Token page", color = Color.White)
    }
}

@Composable
private fun FixtureSection(title: String, selectedIndex: Int, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(title, color = Color.White, style = MaterialTheme.typography.titleMedium)
        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) { content() }
        Text(
            "selected index: $selectedIndex",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

private fun fixtureDockModifier(): Modifier =
    Modifier.fillMaxWidth(0.525f).widthIn(min = 172.dp, max = 217.dp).height(64.dp)

@Composable
private fun FixtureTabContent(
    iconRes: Int,
    label: String,
    contentColor: Color,
    iconWidth: Int = 27,
    iconHeight: Int = 27,
) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Icon(
            painter = painterResource(iconRes),
            contentDescription = null,
            modifier = Modifier.size(width = iconWidth.dp, height = iconHeight.dp),
            tint = contentColor,
        )
        Text(label, color = contentColor, fontSize = 11.sp, lineHeight = 12.sp)
    }
}

@Composable
private fun FixtureBackdrop() {
    Box(
        Modifier.fillMaxSize().background(
            Brush.linearGradient(listOf(Color(0xFF071A35), Color(0xFF241044), Color(0xFF063E4B))),
        ),
    ) {
        Box(
            Modifier.offset(x = (-54).dp, y = 110.dp).size(240.dp)
                .background(Color(0xFF236BFF).copy(alpha = 0.72f), CircleShape),
        )
        Box(
            Modifier.align(Alignment.TopEnd).offset(x = 70.dp, y = 260.dp).size(260.dp)
                .background(Color(0xFFDB38FF).copy(alpha = 0.6f), CircleShape),
        )
        Box(
            Modifier.align(Alignment.BottomCenter).offset(y = 120.dp).size(300.dp)
                .background(Color(0xFF00D8C4).copy(alpha = 0.48f), CircleShape),
        )
        Text(
            "STATIC COLORFUL BACKDROP",
            Modifier.align(Alignment.Center)
                .background(Color.Black.copy(alpha = 0.18f), RoundedCornerShape(16.dp))
                .padding(horizontal = 18.dp, vertical = 12.dp),
            color = Color.White.copy(alpha = 0.82f),
            style = MaterialTheme.typography.labelLarge,
        )
    }
}
