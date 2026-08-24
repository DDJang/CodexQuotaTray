package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.QuotaProgressRing
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.ThemePalette
import com.codexquotatray.android.color
import com.codexquotatray.android.quotaProgressColor

private val realProgressValues = listOf(0, 10, 25, 50, 75, 90, 98, 100)
private val fixedGreenProgressValues = listOf(25, 50, 75, 100)

class QuotaRingFixtureActivity : ComponentActivity() {
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
                QuotaRingFixtureScreen(palette)
            }
        }
    }
}

@Composable
private fun QuotaRingFixtureScreen(palette: ThemePalette) {
    val trackColor = palette.color(palette.progressTrack)
    Box(
        Modifier
            .fillMaxSize()
            .background(palette.color(palette.background)),
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(
                "Quota Ring Fixture",
                color = palette.color(palette.title),
                style = MaterialTheme.typography.headlineSmall,
            )
            QuotaRingFixtureSection(
                title = "Quota colors",
                values = realProgressValues,
                trackColor = trackColor,
                progressColorFor = ::quotaProgressColor,
                palette = palette,
            )
            QuotaRingFixtureSection(
                title = "Fixed green",
                values = fixedGreenProgressValues,
                trackColor = trackColor,
                progressColorFor = { quotaProgressColor(100) },
                palette = palette,
            )
        }
    }
}

@Composable
private fun QuotaRingFixtureSection(
    title: String,
    values: List<Int>,
    trackColor: Color,
    progressColorFor: (Int) -> Color,
    palette: ThemePalette,
) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            title,
            color = palette.color(palette.secondary),
            style = MaterialTheme.typography.titleMedium,
        )
        values.chunked(2).forEach { rowValues ->
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                rowValues.forEach { value ->
                    Box(
                        Modifier.weight(1f),
                        contentAlignment = Alignment.Center,
                    ) {
                        QuotaProgressRing(
                            progress = value / 100f,
                            progressColor = progressColorFor(value),
                            trackColor = trackColor,
                            remainingPercent = value,
                        )
                    }
                }
                repeat(2 - rowValues.size) {
                    Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}
