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
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.SettingsActionButton
import com.codexquotatray.android.SettingsDivider
import com.codexquotatray.android.SettingsGroup
import com.codexquotatray.android.SettingsInfoRow
import com.codexquotatray.android.SettingsSection
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.SecondaryScreenScaffold
import com.codexquotatray.android.SettingsUiTokens
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.color
import com.codexquotatray.android.settingsPalette

private enum class LoginFixtureState(
    val selectorLabel: String,
    val status: String,
) {
    PREPARING("准备", "正在准备登录…"),
    WAITING_FOR_USER("等待", "请在浏览器完成 OpenAI 登录"),
    EXCHANGING("保存", "登录完成，正在保存登录状态…"),
    FAILED("失败", "登录失败，请重试"),
}

private val loginFixtureOptions = LoginFixtureState.entries.mapIndexed { index, state ->
    SettingsSegmentOption(index, state.selectorLabel)
}

class CodexLoginFixtureActivity : ComponentActivity() {
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
                CodexLoginFixtureScreen(onBack = ::finish)
            }
        }
    }
}

@Composable
private fun CodexLoginFixtureScreen(onBack: () -> Unit) {
    var selectedState by remember { mutableIntStateOf(LoginFixtureState.PREPARING.ordinal) }
    var localActionCount by remember { mutableIntStateOf(0) }
    val state = LoginFixtureState.entries[selectedState]
    val palette = com.codexquotatray.android.LocalQuotaPalette.current

    SecondaryScreenScaffold(title = "登录 OpenAI", onBack = onBack) {
        Column(Modifier.fillMaxWidth()) {
            SettingsSection("OpenAI 登录") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsInfoRow(
                        title = "状态",
                        value = state.status,
                        valueColor = if (state == LoginFixtureState.FAILED) {
                            palette.color(palette.error)
                        } else {
                            palette.color(palette.secondary)
                        },
                        valueMaxLines = 4,
                    )
                    if (state == LoginFixtureState.WAITING_FOR_USER) {
                        SettingsDivider()
                        Column(
                            Modifier
                                .fillMaxWidth()
                                .padding(
                                    horizontal = SettingsUiTokens.rowHorizontalPadding,
                                    vertical = 14.dp,
                                ),
                        ) {
                            Text(
                                "登录码",
                                color = palette.color(palette.secondary),
                                fontSize = 13.sp,
                                fontWeight = FontWeight.Medium,
                            )
                            Text(
                                "ABCD-EFGH",
                                modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
                                color = palette.color(palette.accent),
                                fontSize = 25.sp,
                                fontWeight = FontWeight.Bold,
                                fontFamily = FontFamily.Monospace,
                                letterSpacing = 2.sp,
                                textAlign = TextAlign.Center,
                            )
                        }
                        SettingsActionButton(
                            label = "打开浏览器",
                            bottomPadding = SettingsUiTokens.actionEdgeInset,
                            onClick = { localActionCount++ },
                        )
                    }
                    if (state == LoginFixtureState.FAILED) {
                        SettingsActionButton(
                            label = "重新登录",
                            bottomPadding = SettingsUiTokens.actionEdgeInset,
                            onClick = {
                                selectedState = LoginFixtureState.PREPARING.ordinal
                                localActionCount++
                            },
                        )
                    }
                }
            }
            SettingsSection("Debug 场景") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsSegmentedSelector(
                        options = loginFixtureOptions,
                        selectedValue = selectedState,
                        enabled = true,
                        onSelected = { selectedState = it },
                    )
                    SettingsInfoRow("本地操作", "$localActionCount 次")
                }
            }
            Text(
                "仅展示本地 fake 状态；按钮不会打开浏览器、调用 OAuth 或写入账户数据。",
                modifier = Modifier.padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
                color = palette.color(palette.secondary),
            )
        }
    }
}
