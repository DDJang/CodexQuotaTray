package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredHeight
import androidx.compose.foundation.layout.requiredWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.color
import com.codexquotatray.android.liquidglass.UpstreamLiquidBottomTab
import com.codexquotatray.android.liquidglass.UpstreamLiquidBottomTabs
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

private val sourcePriorityLabels = listOf("OpenAI 优先", "Windows 优先")
private val refreshIntervalLabels = listOf("15 分", "30 分", "1 小时")
private val settingsLikeDarkSurface = Color(0xFF252525)

class LiquidSegmentedFixtureActivity : ComponentActivity() {
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
                LiquidSegmentedFixtureScreen()
            }
        }
    }
}

@Composable
private fun LiquidSegmentedFixtureScreen() {
    val backdrop = rememberLayerBackdrop()
    val darkSurfaceBackdrop = rememberLayerBackdrop()
    val scrollState = rememberScrollState()
    var currentTwoSelected by remember { mutableIntStateOf(0) }
    var currentThreeSelected by remember { mutableIntStateOf(1) }
    var upstreamTwoSelected by remember { mutableIntStateOf(0) }
    var upstreamThreeSelected by remember { mutableIntStateOf(1) }
    var darkUpstreamTwoSelected by remember { mutableIntStateOf(1) }
    var darkUpstreamThreeSelected by remember { mutableIntStateOf(2) }
    val palette = com.codexquotatray.android.LocalQuotaPalette.current

    Box(Modifier.fillMaxSize()) {
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
                .background(Color.Black),
        ) {
            FixtureBackdrop()
        }

        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .verticalScroll(scrollState)
                .padding(horizontal = 18.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            Text(
                "Liquid Segmented Fixture",
                color = Color.White,
                style = MaterialTheme.typography.headlineSmall,
            )
            Text(
                "Local state only · tap, hold, drag · Kyant upstream parameters unchanged",
                color = Color.White.copy(alpha = 0.72f),
                style = MaterialTheme.typography.bodyMedium,
            )

            FixtureSection(
                title = "Current Material · 2 options",
                selectedLabel = sourcePriorityLabels[currentTwoSelected],
            ) {
                SettingsSegmentedSelector(
                    options = sourcePriorityLabels.mapIndexed { index, label ->
                        SettingsSegmentOption(index, label)
                    },
                    selectedValue = currentTwoSelected,
                    enabled = true,
                    onSelected = { currentTwoSelected = it },
                )
            }
            FixtureSection(
                title = "Current Material · 3 options",
                selectedLabel = refreshIntervalLabels[currentThreeSelected],
            ) {
                SettingsSegmentedSelector(
                    options = refreshIntervalLabels.mapIndexed { index, label ->
                        SettingsSegmentOption(index, label)
                    },
                    selectedValue = currentThreeSelected,
                    enabled = true,
                    onSelected = { currentThreeSelected = it },
                )
            }
            FixtureSection(
                title = "Scaled exact upstream · 0.75 · 2 options",
                selectedLabel = sourcePriorityLabels[upstreamTwoSelected],
            ) {
                ScaledUpstreamSegmentedTabs(
                    labels = sourcePriorityLabels,
                    selectedIndex = upstreamTwoSelected,
                    onSelected = { upstreamTwoSelected = it },
                    backdrop = backdrop,
                )
            }
            FixtureSection(
                title = "Scaled exact upstream · 0.75 · 3 options",
                selectedLabel = refreshIntervalLabels[upstreamThreeSelected],
            ) {
                ScaledUpstreamSegmentedTabs(
                    labels = refreshIntervalLabels,
                    selectedIndex = upstreamThreeSelected,
                    onSelected = { upstreamThreeSelected = it },
                    backdrop = backdrop,
                )
            }

            Text(
                "Settings-like dark surface",
                color = Color.White,
                style = MaterialTheme.typography.titleMedium,
            )
            Box(Modifier.fillMaxWidth()) {
                // The dark surface itself is part of the source consumed by both controls.
                Box(
                    Modifier
                        .matchParentSize()
                        .layerBackdrop(darkSurfaceBackdrop)
                        .background(settingsLikeDarkSurface, RoundedCornerShape(24.dp)),
                )
                Column(
                    Modifier.padding(vertical = 14.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    FixtureSection(
                        title = "Scaled exact upstream · 0.75 · 2 options",
                        selectedLabel = sourcePriorityLabels[darkUpstreamTwoSelected],
                        titleColor = palette.color(palette.body),
                        selectedColor = palette.color(palette.secondary),
                    ) {
                        ScaledUpstreamSegmentedTabs(
                            labels = sourcePriorityLabels,
                            selectedIndex = darkUpstreamTwoSelected,
                            onSelected = { darkUpstreamTwoSelected = it },
                            backdrop = darkSurfaceBackdrop,
                            contentColor = palette.color(palette.body),
                        )
                    }
                    FixtureSection(
                        title = "Scaled exact upstream · 0.75 · 3 options",
                        selectedLabel = refreshIntervalLabels[darkUpstreamThreeSelected],
                        titleColor = palette.color(palette.body),
                        selectedColor = palette.color(palette.secondary),
                    ) {
                        ScaledUpstreamSegmentedTabs(
                            labels = refreshIntervalLabels,
                            selectedIndex = darkUpstreamThreeSelected,
                            onSelected = { darkUpstreamThreeSelected = it },
                            backdrop = darkSurfaceBackdrop,
                            contentColor = palette.color(palette.body),
                        )
                    }
                }
            }

            Text(
                "All upstream 2/3 option controls share one opaque page backdrop. No quota, token, OAuth, LAN, worker, or network access.",
                color = Color.White.copy(alpha = 0.72f),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun FixtureSection(
    title: String,
    selectedLabel: String,
    titleColor: Color = Color.White,
    selectedColor: Color = Color.White.copy(alpha = 0.7f),
    content: @Composable () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(title, color = titleColor, style = MaterialTheme.typography.titleMedium)
        content()
        Text(
            "selected = $selectedLabel",
            color = selectedColor,
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun ScaledUpstreamSegmentedTabs(
    labels: List<String>,
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: com.kyant.backdrop.Backdrop,
    contentColor: Color = Color.White,
) {
    val scale = 0.75f
    var requestedIndex by remember { mutableIntStateOf(selectedIndex) }
    LaunchedEffect(selectedIndex) {
        requestedIndex = selectedIndex
    }

    val requestedIndexState = rememberUpdatedState(requestedIndex)
    val committedSelectedIndex = rememberUpdatedState(selectedIndex)
    val selectionSink = rememberUpdatedState(onSelected)
    val selectedProvider = remember { { requestedIndexState.value } }
    val hapticFeedback = LocalHapticFeedback.current
    val stableSelectionSink = remember {
        { index: Int ->
            if (committedSelectedIndex.value != index) {
                hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                selectionSink.value(index)
            }
        }
    }

    BoxWithConstraints(
        modifier = Modifier
            .fillMaxWidth()
            .height(48.dp)
            .padding(horizontal = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        val logicalWidth = maxWidth / scale
        Box(
            Modifier
                .requiredWidth(logicalWidth)
                .requiredHeight(64.dp)
                .graphicsLayer {
                    transformOrigin = TransformOrigin.Center
                    scaleX = scale
                    scaleY = scale
                },
        ) {
            UpstreamLiquidBottomTabs(
                selectedTabIndex = selectedProvider,
                onTabSelected = stableSelectionSink,
                backdrop = backdrop,
                tabsCount = labels.size,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(64.dp),
            ) {
                labels.forEachIndexed { index, label ->
                    UpstreamLiquidBottomTab(onClick = { requestedIndex = index }) {
                        Text(
                            label,
                            color = contentColor,
                            fontSize = 19.sp,
                            maxLines = 1,
                        )
                    }
                }
            }
        }
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
                        Color(0xFF06152D),
                        Color(0xFF291044),
                        Color(0xFF063E4B),
                    ),
                ),
            ),
    ) {
        Box(
            Modifier
                .offset(x = (-70).dp, y = 120.dp)
                .size(260.dp)
                .background(Color(0xFF246BFF).copy(alpha = 0.74f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.TopEnd)
                .offset(x = 80.dp, y = 250.dp)
                .size(280.dp)
                .background(Color(0xFFE238FF).copy(alpha = 0.62f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.BottomCenter)
                .offset(y = 150.dp)
                .size(330.dp)
                .background(Color(0xFF00D8C4).copy(alpha = 0.5f), CircleShape),
        )
        Text(
            "STATIC COLORFUL BACKDROP",
            Modifier
                .align(Alignment.Center)
                .background(Color.Black.copy(alpha = 0.18f), RoundedCornerShape(16.dp))
                .padding(horizontal = 18.dp, vertical = 12.dp),
            color = Color.White.copy(alpha = 0.82f),
            style = MaterialTheme.typography.labelLarge,
        )
    }
}
