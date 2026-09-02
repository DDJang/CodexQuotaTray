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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexConfirmDialog
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.SettingsActionButton
import com.codexquotatray.android.SettingsDivider
import com.codexquotatray.android.SettingsInfoRow
import com.codexquotatray.android.SettingsSection
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.SettingsGroup
import com.codexquotatray.android.SecondaryScreenScaffold
import com.codexquotatray.android.SettingsUiTokens
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.color
import com.codexquotatray.android.settingsPalette

private enum class PairingFixtureState(
    val selectorLabel: String,
) {
    UNPAIRED("未配对"),
    PAIRED("已配对"),
}

private val pairingFixtureOptions = PairingFixtureState.entries.mapIndexed { index, state ->
    SettingsSegmentOption(index, state.selectorLabel)
}

class WindowsPairingFixtureActivity : ComponentActivity() {
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
                WindowsPairingFixtureScreen(onBack = ::finish)
            }
        }
    }
}

@Composable
private fun WindowsPairingFixtureScreen(onBack: () -> Unit) {
    var selectedState by remember { mutableIntStateOf(PairingFixtureState.UNPAIRED.ordinal) }
    var localActionCount by remember { mutableIntStateOf(0) }
    var showConfirmDialog by remember { mutableStateOf(false) }
    val state = PairingFixtureState.entries[selectedState]
    val palette = com.codexquotatray.android.LocalQuotaPalette.current

    SecondaryScreenScaffold(title = "Windows 配对", onBack = onBack) {
        Column(Modifier.fillMaxWidth()) {
            SettingsSection("Windows") {
                SettingsGroup(allowLiquidOverflow = true) {
                    if (state == PairingFixtureState.UNPAIRED) {
                        SettingsInfoRow("状态", "未配对")
                        SettingsActionButton(
                            label = "扫码配对",
                            bottomPadding = SettingsUiTokens.actionEdgeInset,
                            onClick = { localActionCount++ },
                        )
                    } else {
                        SettingsInfoRow("电脑", "Windows PC")
                        SettingsDivider()
                        SettingsInfoRow("地址", "192.168.1.58:43127")
                        SettingsDivider()
                        SettingsInfoRow("最近连接成功", "2 分钟前")
                        SettingsActionButton(
                            label = "重新扫码配对",
                            onClick = { localActionCount++ },
                        )
                        SettingsActionButton(
                            label = "复制诊断信息",
                            onClick = { localActionCount++ },
                        )
                        SettingsActionButton(
                            label = "解除配对",
                            danger = true,
                            bottomPadding = SettingsUiTokens.actionEdgeInset,
                            onClick = { showConfirmDialog = true },
                        )
                    }
                }
            }
            SettingsSection("Debug 场景") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsSegmentedSelector(
                        options = pairingFixtureOptions,
                        selectedValue = selectedState,
                        enabled = true,
                        onSelected = {
                            selectedState = it
                            showConfirmDialog = false
                        },
                    )
                    SettingsInfoRow("本地操作", "$localActionCount 次")
                }
            }
            Text(
                "仅展示本地 fake pairing；不会扫码、连接 Windows、复制真实 diagnostics 或写入 pairing store。",
                modifier = Modifier.padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
                color = palette.color(palette.secondary),
            )
        }
    }

    if (showConfirmDialog) {
        CodexConfirmDialog(
            title = "解除配对",
            message = "这是 Debug fixture 的本地确认，不会修改真实配对。",
            confirmText = "解除",
            onConfirm = {
                selectedState = PairingFixtureState.UNPAIRED.ordinal
                showConfirmDialog = false
                localActionCount++
            },
            onDismiss = { showConfirmDialog = false },
        )
    }
}
