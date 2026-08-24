package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
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
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
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
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

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
        setContent {
            CodexQuotaTheme(palette) {
                LiquidBottomTabsFixtureScreen(palette)
            }
        }
    }
}

@Composable
private fun LiquidBottomTabsFixtureScreen(palette: ThemePalette) {
    val backdrop = rememberLayerBackdrop()
    var upstreamThreeSelected by remember { mutableIntStateOf(0) }
    var upstreamCodexSelected by remember { mutableIntStateOf(0) }
    var productionSelected by remember { mutableIntStateOf(0) }
    val upstreamThreeSelectedState = rememberUpdatedState(upstreamThreeSelected)
    val upstreamCodexSelectedState = rememberUpdatedState(upstreamCodexSelected)
    val productionSelectedState = rememberUpdatedState(productionSelected)
    val upstreamThreeProvider = remember { { upstreamThreeSelectedState.value } }
    val upstreamCodexProvider = remember { { upstreamCodexSelectedState.value } }
    val productionProvider = remember { { productionSelectedState.value } }
    val contentColor = palette.color(palette.body)

    Box(
        Modifier
            .fillMaxSize()
            .background(palette.color(palette.background)),
    ) {
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop),
        ) {
            FixtureBackdrop()
        }
        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 18.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(24.dp),
        ) {
            Text(
                "Liquid Bottom Tabs Fixture",
                color = Color.White,
                style = MaterialTheme.typography.headlineSmall,
            )
            Text(
                "Same colorful backdrop · tap, hold, drag",
                color = Color.White.copy(alpha = 0.7f),
                style = MaterialTheme.typography.bodyMedium,
            )
            FixtureSection(
                title = "Kyant upstream · 3 tabs",
                selectedIndex = upstreamThreeSelected,
            ) {
                UpstreamLiquidBottomTabs(
                    selectedTabIndex = upstreamThreeProvider,
                    onTabSelected = { upstreamThreeSelected = it },
                    backdrop = backdrop,
                    tabsCount = 3,
                    modifier = fixtureDockModifier(),
                ) {
                    UpstreamLiquidBottomTab(onClick = { upstreamThreeSelected = 0 }) {
                        FixtureTabContent(R.drawable.ic_quota_tray, "A", contentColor)
                    }
                    UpstreamLiquidBottomTab(onClick = { upstreamThreeSelected = 1 }) {
                        FixtureTabContent(R.drawable.ic_usage, "B", contentColor)
                    }
                    UpstreamLiquidBottomTab(onClick = { upstreamThreeSelected = 2 }) {
                        FixtureTabContent(R.drawable.ic_settings, "C", contentColor)
                    }
                }
            }
            FixtureSection(
                title = "Kyant upstream · 2 tabs / Codex geometry",
                selectedIndex = upstreamCodexSelected,
            ) {
                UpstreamLiquidBottomTabs(
                    selectedTabIndex = upstreamCodexProvider,
                    onTabSelected = { upstreamCodexSelected = it },
                    backdrop = backdrop,
                    tabsCount = 2,
                    modifier = fixtureDockModifier(),
                ) {
                    UpstreamLiquidBottomTab(onClick = { upstreamCodexSelected = 0 }) {
                        FixtureTabContent(
                            R.drawable.ic_quota_tray,
                            "额度",
                            contentColor,
                            iconWidth = 22.dp,
                            iconHeight = 24.dp,
                        )
                    }
                    UpstreamLiquidBottomTab(onClick = { upstreamCodexSelected = 1 }) {
                        FixtureTabContent(R.drawable.ic_usage, "统计", contentColor)
                    }
                }
            }
            FixtureSection(
                title = "Current production",
                selectedIndex = productionSelected,
            ) {
                LiquidBottomTabs(
                    selectedTabIndex = productionProvider,
                    onTabSelected = { productionSelected = it },
                    backdrop = backdrop,
                    tabsCount = 2,
                    modifier = fixtureDockModifier(),
                ) {
                    LiquidBottomTab(onClick = { productionSelected = 0 }) {
                        FixtureTabContent(
                            R.drawable.ic_quota_tray,
                            "额度",
                            contentColor,
                            iconWidth = 22.dp,
                            iconHeight = 24.dp,
                        )
                    }
                    LiquidBottomTab(onClick = { productionSelected = 1 }) {
                        FixtureTabContent(R.drawable.ic_usage, "统计", contentColor)
                    }
                }
            }
            Text(
                "A/B/C use one page backdrop source; no quota, token, OAuth, LAN, worker, or network access.",
                color = Color.White.copy(alpha = 0.7f),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun FixtureSection(
    title: String,
    selectedIndex: Int,
    content: @Composable () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            title,
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Box(
            Modifier.fillMaxWidth(),
            contentAlignment = Alignment.Center,
        ) {
            content()
        }
        Text(
            "selected index: $selectedIndex",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

private fun fixtureDockModifier(): Modifier =
    Modifier
        .fillMaxWidth(0.525f)
        .widthIn(min = 172.dp, max = 217.dp)
        .height(64.dp)

@Composable
private fun FixtureTabContent(
    iconRes: Int,
    label: String,
    contentColor: Color,
    iconWidth: androidx.compose.ui.unit.Dp = 27.dp,
    iconHeight: androidx.compose.ui.unit.Dp = 27.dp,
) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Icon(
            painter = painterResource(iconRes),
            contentDescription = null,
            modifier = Modifier.size(width = iconWidth, height = iconHeight),
            tint = contentColor,
        )
        Text(
            label,
            color = contentColor,
            fontSize = 11.sp,
            lineHeight = 12.sp,
        )
    }
}

@Composable
private fun FixtureBackdrop() {
    Box(
        Modifier
            .fillMaxSize()
            .background(
                Brush.linearGradient(
                    listOf(
                        Color(0xFF071A35),
                        Color(0xFF241044),
                        Color(0xFF063E4B),
                    ),
                ),
            ),
    ) {
        Box(
            Modifier
                .offset(x = (-54).dp, y = 110.dp)
                .size(240.dp)
                .background(Color(0xFF236BFF).copy(alpha = 0.72f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.TopEnd)
                .offset(x = 70.dp, y = 260.dp)
                .size(260.dp)
                .background(Color(0xFFDB38FF).copy(alpha = 0.6f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.BottomCenter)
                .offset(y = 120.dp)
                .size(300.dp)
                .background(Color(0xFF00D8C4).copy(alpha = 0.48f), CircleShape),
        )
        Text(
            "STATIC COLORFUL BACKDROP",
            Modifier
                .align(Alignment.Center)
                .background(
                    Color.Black.copy(alpha = 0.18f),
                    RoundedCornerShape(16.dp),
                )
                .padding(horizontal = 18.dp, vertical = 12.dp),
            color = Color.White.copy(alpha = 0.82f),
            style = MaterialTheme.typography.labelLarge,
        )
    }
}
