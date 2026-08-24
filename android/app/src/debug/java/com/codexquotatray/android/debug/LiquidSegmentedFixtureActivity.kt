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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.SettingsUiTokens
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.color
import com.codexquotatray.android.liquidglass.LiquidSegmentedTabs
import com.kyant.backdrop.backdrops.LayerBackdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

private val sourcePriorityLabels = listOf("OpenAI 优先", "Windows 优先")
private val refreshIntervalLabels = listOf("15 分", "30 分", "1 小时")
private val resetCreditExpiryLeadLabels = listOf("1 天", "6 小时", "1 小时")
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
    val productionTopologyExactBackdrop = rememberLayerBackdrop()
    val productionTopologyFullSlotBackdrop = rememberLayerBackdrop()
    val materialAncestorBackdrop = rememberLayerBackdrop()
    val roundedAncestorBackdrop = rememberLayerBackdrop()
    val scrollState = rememberScrollState()
    var currentTwoSelected by remember { mutableIntStateOf(0) }
    var currentThreeSelected by remember { mutableIntStateOf(1) }
    var productionTopologyExactSelected by remember { mutableIntStateOf(0) }
    var productionTopologyFullSlotSelected by remember { mutableIntStateOf(0) }
    var materialAncestorSelected by remember { mutableIntStateOf(0) }
    var roundedAncestorSelected by remember { mutableIntStateOf(0) }
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

            Text(
                "Production topology regression · source bounds only",
                color = Color.White,
                style = MaterialTheme.typography.titleMedium,
            )
            ProductionTopologyRegressionCase(
                title = "A · exact-bounds local source · expected edge artifact",
                labels = resetCreditExpiryLeadLabels,
                selectedIndex = productionTopologyExactSelected,
                onSelected = { productionTopologyExactSelected = it },
                backdrop = productionTopologyExactBackdrop,
                fullSlotSource = false,
                sourceColor = palette.color(palette.surface),
                accentColor = palette.color(palette.accent),
                contentColor = palette.color(palette.body),
            )
            ProductionTopologyRegressionCase(
                title = "B · full-slot local source · expected clean edges",
                labels = resetCreditExpiryLeadLabels,
                selectedIndex = productionTopologyFullSlotSelected,
                onSelected = { productionTopologyFullSlotSelected = it },
                backdrop = productionTopologyFullSlotBackdrop,
                fullSlotSource = true,
                sourceColor = palette.color(palette.surface),
                accentColor = palette.color(palette.accent),
                contentColor = palette.color(palette.body),
            )

            Text(
                "Ancestor clip regression · full-slot source held constant",
                color = Color.White,
                style = MaterialTheme.typography.titleMedium,
            )
            AncestorClipRegressionCase(
                title = "A · Material Card ancestor · clips",
                labels = resetCreditExpiryLeadLabels,
                selectedIndex = materialAncestorSelected,
                onSelected = { materialAncestorSelected = it },
                backdrop = materialAncestorBackdrop,
                materialCardAncestor = true,
                surfaceColor = palette.color(palette.surface),
                accentColor = palette.color(palette.accent),
                contentColor = palette.color(palette.body),
            )
            AncestorClipRegressionCase(
                title = "B · rounded background ancestor · non-clipping",
                labels = resetCreditExpiryLeadLabels,
                selectedIndex = roundedAncestorSelected,
                onSelected = { roundedAncestorSelected = it },
                backdrop = roundedAncestorBackdrop,
                materialCardAncestor = false,
                surfaceColor = palette.color(palette.surface),
                accentColor = palette.color(palette.accent),
                contentColor = palette.color(palette.body),
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
                title = "Kyant geometry adaptation · 0.75 · 2 options",
                selectedLabel = sourcePriorityLabels[upstreamTwoSelected],
            ) {
                KyantLiquidSegmentedAdaptation(
                    labels = sourcePriorityLabels,
                    selectedIndex = upstreamTwoSelected,
                    onSelected = { upstreamTwoSelected = it },
                    backdrop = backdrop,
                    modifier = Modifier.padding(horizontal = 12.dp),
                )
            }
            FixtureSection(
                title = "Kyant geometry adaptation · 0.75 · 3 options",
                selectedLabel = refreshIntervalLabels[upstreamThreeSelected],
            ) {
                KyantLiquidSegmentedAdaptation(
                    labels = refreshIntervalLabels,
                    selectedIndex = upstreamThreeSelected,
                    onSelected = { upstreamThreeSelected = it },
                    backdrop = backdrop,
                    modifier = Modifier.padding(horizontal = 12.dp),
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
                        title = "Kyant geometry adaptation · 0.75 · 2 options",
                        selectedLabel = sourcePriorityLabels[darkUpstreamTwoSelected],
                        titleColor = palette.color(palette.body),
                        selectedColor = palette.color(palette.secondary),
                    ) {
                        KyantLiquidSegmentedAdaptation(
                            labels = sourcePriorityLabels,
                            selectedIndex = darkUpstreamTwoSelected,
                            onSelected = { darkUpstreamTwoSelected = it },
                            backdrop = darkSurfaceBackdrop,
                            contentColor = palette.color(palette.body),
                            modifier = Modifier.padding(horizontal = 12.dp),
                        )
                    }
                    FixtureSection(
                        title = "Kyant geometry adaptation · 0.75 · 3 options",
                        selectedLabel = refreshIntervalLabels[darkUpstreamThreeSelected],
                        titleColor = palette.color(palette.body),
                        selectedColor = palette.color(palette.secondary),
                    ) {
                        KyantLiquidSegmentedAdaptation(
                            labels = refreshIntervalLabels,
                            selectedIndex = darkUpstreamThreeSelected,
                            onSelected = { darkUpstreamThreeSelected = it },
                            backdrop = darkSurfaceBackdrop,
                            contentColor = palette.color(palette.body),
                            modifier = Modifier.padding(horizontal = 12.dp),
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
private fun ProductionTopologyRegressionCase(
    title: String,
    labels: List<String>,
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: LayerBackdrop,
    fullSlotSource: Boolean,
    sourceColor: Color,
    accentColor: Color,
    contentColor: Color,
) {
    val horizontalInset = 12.dp
    val verticalInset = 10.dp
    val controlHeight = 48.dp

    FixtureSection(
        title = title,
        selectedLabel = labels[selectedIndex],
        titleColor = Color.White,
        selectedColor = Color.White.copy(alpha = 0.7f),
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(controlHeight + verticalInset * 2),
        ) {
            val controlModifier = Modifier
                .align(Alignment.Center)
                .fillMaxWidth()
                .padding(horizontal = horizontalInset)
                .height(controlHeight)
            val sourceModifier = if (fullSlotSource) {
                Modifier.fillMaxSize()
            } else {
                controlModifier
            }
            Box(
                sourceModifier
                    .layerBackdrop(backdrop)
                    .background(sourceColor),
            )
            LiquidSegmentedTabs(
                labels = labels,
                selectedIndex = selectedIndex,
                onSelected = onSelected,
                backdrop = backdrop,
                accentColor = accentColor,
                contentColor = contentColor,
                modifier = controlModifier,
            )
        }
    }
}

@Composable
private fun AncestorClipRegressionCase(
    title: String,
    labels: List<String>,
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: LayerBackdrop,
    materialCardAncestor: Boolean,
    surfaceColor: Color,
    accentColor: Color,
    contentColor: Color,
) {
    val groupShape = RoundedCornerShape(SettingsUiTokens.groupCornerRadius)
    FixtureSection(
        title = title,
        selectedLabel = labels[selectedIndex],
    ) {
        if (materialCardAncestor) {
            Card(
                modifier = Modifier.fillMaxWidth(),
                shape = groupShape,
                colors = CardDefaults.cardColors(containerColor = surfaceColor),
            ) {
                AncestorClipRegressionContent(
                    labels = labels,
                    selectedIndex = selectedIndex,
                    onSelected = onSelected,
                    backdrop = backdrop,
                    surfaceColor = surfaceColor,
                    accentColor = accentColor,
                    contentColor = contentColor,
                )
            }
        } else {
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(surfaceColor, groupShape),
            ) {
                AncestorClipRegressionContent(
                    labels = labels,
                    selectedIndex = selectedIndex,
                    onSelected = onSelected,
                    backdrop = backdrop,
                    surfaceColor = surfaceColor,
                    accentColor = accentColor,
                    contentColor = contentColor,
                )
            }
        }
    }
}

@Composable
private fun AncestorClipRegressionContent(
    labels: List<String>,
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: LayerBackdrop,
    surfaceColor: Color,
    accentColor: Color,
    contentColor: Color,
) {
    val horizontalInset = SettingsUiTokens.actionHorizontalInset
    val verticalInset = SettingsUiTokens.segmentedBottomInset
    val controlHeight = SettingsUiTokens.segmentedHeight
    Column(
        Modifier
            .fillMaxWidth()
            .padding(vertical = SettingsUiTokens.groupVerticalPadding),
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(controlHeight + verticalInset * 2),
        ) {
            Box(
                Modifier
                    .fillMaxSize()
                    .layerBackdrop(backdrop)
                    .background(surfaceColor),
            )
            LiquidSegmentedTabs(
                labels = labels,
                selectedIndex = selectedIndex,
                onSelected = onSelected,
                backdrop = backdrop,
                accentColor = accentColor,
                contentColor = contentColor,
                modifier = Modifier
                    .align(Alignment.Center)
                    .fillMaxWidth()
                    .padding(horizontal = horizontalInset)
                    .height(controlHeight),
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
