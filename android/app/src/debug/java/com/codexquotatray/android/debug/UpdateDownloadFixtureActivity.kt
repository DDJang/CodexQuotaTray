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
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.SettingsActionButton
import com.codexquotatray.android.SettingsGroup
import com.codexquotatray.android.SettingsInfoRow
import com.codexquotatray.android.SettingsSection
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.SecondaryScreenScaffold
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.UpdateAvailableDialog
import com.codexquotatray.android.color
import com.codexquotatray.android.settingsPalette
import com.codexquotatray.android.update.SemVer
import com.codexquotatray.android.update.UpdateAsset
import com.codexquotatray.android.update.UpdateDownloadPhase
import com.codexquotatray.android.update.UpdateDownloadProgress
import com.codexquotatray.android.update.UpdateRelease

private enum class DownloadFixtureState(
    val selectorLabel: String,
    val displayLabel: String,
) {
    AVAILABLE("可用", "Available"),
    DOWNLOADING_INDETERMINATE("下载中", "Downloading indeterminate"),
    DOWNLOADING_PROGRESS("进度", "Downloading progress"),
    VERIFYING("校验", "Verifying"),
    FAILED("失败", "Failed"),
}

private val downloadFixtureOptions = DownloadFixtureState.entries.mapIndexed { index, state ->
    SettingsSegmentOption(index, state.selectorLabel)
}

private val fixtureRelease = UpdateRelease(
    tagName = "android-v9.9.9-fixture",
    name = "Android 9.9.9 fixture",
    notes = "## Fixture release\n\nThis release notes block is local-only preview content.",
    publishedAt = "2026-09-02T00:00:00Z",
    version = SemVer(9, 9, 9),
    androidAsset = UpdateAsset(
        name = "CodexQuotaTray-Android-v9.9.9-fixture.apk",
        browserDownloadUrl = "https://example.invalid/codexquotatray-fixture.apk",
    ),
)

class UpdateDownloadFixtureActivity : ComponentActivity() {
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
                UpdateDownloadFixtureScreen(onBack = ::finish)
            }
        }
    }
}

@Composable
private fun UpdateDownloadFixtureScreen(onBack: () -> Unit) {
    var selectedState by remember { mutableIntStateOf(DownloadFixtureState.AVAILABLE.ordinal) }
    var dialogVisible by remember { mutableStateOf(true) }
    var localActionCount by remember { mutableIntStateOf(0) }
    val state = DownloadFixtureState.entries[selectedState]
    val palette = com.codexquotatray.android.LocalQuotaPalette.current
    val progress = when (state) {
        DownloadFixtureState.AVAILABLE -> UpdateDownloadProgress.Idle
        DownloadFixtureState.DOWNLOADING_INDETERMINATE -> UpdateDownloadProgress(
            phase = UpdateDownloadPhase.DOWNLOADING,
        )
        DownloadFixtureState.DOWNLOADING_PROGRESS -> UpdateDownloadProgress(
            phase = UpdateDownloadPhase.DOWNLOADING,
            bytesDownloaded = 4_200_000L,
            totalBytes = 10_000_000L,
            bytesPerSecond = 1_500_000.0,
        )
        DownloadFixtureState.VERIFYING -> UpdateDownloadProgress(
            phase = UpdateDownloadPhase.VERIFYING,
            bytesDownloaded = 10_000_000L,
            totalBytes = 10_000_000L,
        )
        DownloadFixtureState.FAILED -> UpdateDownloadProgress(UpdateDownloadPhase.FAILED)
    }
    val downloading = state == DownloadFixtureState.DOWNLOADING_INDETERMINATE ||
        state == DownloadFixtureState.DOWNLOADING_PROGRESS ||
        state == DownloadFixtureState.VERIFYING
    val downloadError = if (state == DownloadFixtureState.FAILED) {
        "Fixture simulated failure"
    } else {
        null
    }

    SecondaryScreenScaffold(title = "更新下载", onBack = onBack) {
        Column(Modifier.fillMaxWidth()) {
            SettingsSection("Debug 场景") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsSegmentedSelector(
                        options = downloadFixtureOptions,
                        selectedValue = selectedState,
                        enabled = true,
                        onSelected = {
                            selectedState = it
                            dialogVisible = true
                        },
                    )
                    SettingsInfoRow("当前场景", state.displayLabel)
                    SettingsInfoRow("本地操作", "$localActionCount 次")
                }
            }
            SettingsActionButton(
                label = "打开更新弹窗",
                onClick = { dialogVisible = true },
            )
            Text(
                "仅复用正式 UpdateAvailableDialog；所有 callback 只修改 Debug 状态，不会联网、下载或安装 APK。",
                modifier = Modifier.padding(horizontal = 20.dp),
                color = palette.color(palette.secondary),
            )
        }
    }

    if (dialogVisible) {
        UpdateAvailableDialog(
            release = fixtureRelease,
            currentVersion = "0.11.0",
            downloading = downloading,
            progress = progress,
            downloadError = downloadError,
            onLater = { dialogVisible = false },
            onDownload = {
                selectedState = DownloadFixtureState.DOWNLOADING_INDETERMINATE.ordinal
                localActionCount++
            },
            onCancel = {
                selectedState = DownloadFixtureState.AVAILABLE.ordinal
                localActionCount++
            },
            onBrowserDownload = { localActionCount++ },
        )
    }
}
