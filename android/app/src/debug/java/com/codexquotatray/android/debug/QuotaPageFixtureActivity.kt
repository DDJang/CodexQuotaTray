package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.QuotaPageContent
import com.codexquotatray.android.SecondaryScreenScaffold
import com.codexquotatray.android.SettingsGroup
import com.codexquotatray.android.SettingsInfoRow
import com.codexquotatray.android.SettingsSection
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.SettingsUiTokens
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.color
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.settingsPalette
import com.codexquotatray.android.ui.QuotaCardModel
import com.codexquotatray.android.ui.QuotaUiModel
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.ui.ResetCreditDetailState
import com.codexquotatray.android.ui.ResetCreditUiModel
import com.codexquotatray.android.ui.quotaErrorUiModel
import com.codexquotatray.android.ui.quotaLoadingUiModel
import com.codexquotatray.android.ui.unauthenticatedQuotaUiModel

private const val QUOTA_FIXTURE_NOW_MILLIS = 1_788_321_600_000L
private const val QUOTA_FIXTURE_NOW_SECONDS = QUOTA_FIXTURE_NOW_MILLIS / 1_000L

private enum class QuotaFixtureScenario(
    val selectorLabel: String,
    val displayLabel: String,
) {
    UNAUTHENTICATED("未登录", "Unauthenticated"),
    LOADING_NO_CACHE("加载中", "Loading no cache"),
    LOADED_SINGLE("单窗口", "Loaded single 5h · 72% remaining"),
    LOADED_DUAL("双窗口", "Loaded dual 5h + 7d"),
    RESET_CREDITS("重置卡", "Reset credits"),
    ERROR_WITH_CACHE("缓存错误", "Error with previous cache"),
    EMPTY_LOADED("空额度", "Empty loaded windows"),
}

private val quotaFixtureOptions = QuotaFixtureScenario.entries.mapIndexed { index, scenario ->
    SettingsSegmentOption(index, scenario.selectorLabel)
}

private val quotaFixtureSingle = QuotaUiModel(
    status = QuotaUiStatus.LOADED,
    accountLabel = "Plus",
    windows = listOf(
        QuotaCardModel(
            title = "5 小时窗口",
            remainingPercent = 72,
            usedPercent = 28,
            windowDurationMins = 5 * 60L,
            resetsAt = QUOTA_FIXTURE_NOW_SECONDS + 2 * 60 * 60L,
        ),
    ),
    updatedAtMillis = QUOTA_FIXTURE_NOW_MILLIS,
    source = QuotaSource.DIRECT,
)

private val quotaFixtureDual = quotaFixtureSingle.copy(
    windows = listOf(
        quotaFixtureSingle.windows.first(),
        QuotaCardModel(
            title = "7 天窗口",
            remainingPercent = 45,
            usedPercent = 55,
            windowDurationMins = 7 * 24 * 60L,
            resetsAt = QUOTA_FIXTURE_NOW_SECONDS + 5 * 24 * 60 * 60L,
        ),
    ),
)

private val quotaFixtureResetCredits = ResetCreditUiModel(
    availableCount = 2L,
    availableCredits = listOf(
        ResetCredit(
            id = "fixture-reset-1",
            status = "available",
            expiresAt = QUOTA_FIXTURE_NOW_SECONDS + 2 * 24 * 60 * 60L,
        ),
        ResetCredit(
            id = "fixture-reset-2",
            status = "available",
            expiresAt = QUOTA_FIXTURE_NOW_SECONDS + 6 * 24 * 60 * 60L,
        ),
    ),
    detailState = ResetCreditDetailState.COMPLETE,
)

private val quotaFixtureEmpty = QuotaUiModel(
    status = QuotaUiStatus.LOADED,
    accountLabel = "Plus",
    updatedAtMillis = QUOTA_FIXTURE_NOW_MILLIS,
    source = QuotaSource.DIRECT,
    message = "暂无可用额度",
)

private fun quotaFixtureModel(scenario: QuotaFixtureScenario): QuotaUiModel = when (scenario) {
    QuotaFixtureScenario.UNAUTHENTICATED -> unauthenticatedQuotaUiModel()
    QuotaFixtureScenario.LOADING_NO_CACHE -> quotaLoadingUiModel()
    QuotaFixtureScenario.LOADED_SINGLE -> quotaFixtureSingle
    QuotaFixtureScenario.LOADED_DUAL -> quotaFixtureDual
    QuotaFixtureScenario.RESET_CREDITS -> quotaFixtureSingle.copy(resetCredits = quotaFixtureResetCredits)
    QuotaFixtureScenario.ERROR_WITH_CACHE -> quotaErrorUiModel(
        message = "无法连接 OpenAI",
        previous = quotaFixtureDual,
    )
    QuotaFixtureScenario.EMPTY_LOADED -> quotaFixtureEmpty
}

class QuotaPageFixtureActivity : ComponentActivity() {
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
        val effectiveTheme = AppTheme.effectiveMode(this, selectedTheme)
        val palette = settingsPalette(AppTheme.palette(this, selectedTheme), effectiveTheme)
        setContent {
            CodexQuotaTheme(palette) {
                QuotaPageFixtureScreen(onBack = ::finish)
            }
        }
    }
}

@Composable
private fun QuotaPageFixtureScreen(onBack: () -> Unit) {
    var selectedScenario by remember { mutableIntStateOf(QuotaFixtureScenario.UNAUTHENTICATED.ordinal) }
    var localActionCount by remember { mutableIntStateOf(0) }
    val scenario = QuotaFixtureScenario.entries[selectedScenario]
    val model = quotaFixtureModel(scenario)
    val palette = com.codexquotatray.android.LocalQuotaPalette.current

    SecondaryScreenScaffold(title = "额度页面", onBack = onBack) {
        Column(Modifier.fillMaxWidth()) {
            SettingsSection("Debug 场景") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsSegmentedSelector(
                        options = quotaFixtureOptions,
                        selectedValue = selectedScenario,
                        enabled = true,
                        onSelected = { selectedScenario = it },
                    )
                    SettingsInfoRow("当前场景", scenario.displayLabel, valueMaxLines = 2)
                    SettingsInfoRow("本地操作", "$localActionCount 次")
                }
            }
            QuotaPageContent(
                model = model,
                busy = false,
                onLogin = { localActionCount++ },
                onPairing = { localActionCount++ },
                modifier = Modifier.fillMaxWidth(),
            )
            Text(
                "仅展示本地 fake 额度状态；不会调用 OAuth、额度服务、Windows LAN、通知或持久化。",
                modifier = Modifier.padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
                color = palette.color(palette.secondary),
            )
        }
    }
}
