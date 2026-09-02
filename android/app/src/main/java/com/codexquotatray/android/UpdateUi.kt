package com.codexquotatray.android

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.codexquotatray.android.update.UpdateRelease
import com.codexquotatray.android.update.UpdateDownloadFormatting
import com.codexquotatray.android.update.UpdateDownloadPhase
import com.codexquotatray.android.update.UpdateDownloadProgress
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

@Composable
internal fun UpdateAvailableDialog(
    release: UpdateRelease,
    currentVersion: String,
    downloading: Boolean,
    progress: UpdateDownloadProgress,
    downloadError: String?,
    onLater: () -> Unit,
    onDownload: () -> Unit,
    onCancel: () -> Unit,
    onBrowserDownload: () -> Unit,
) {
    val hostPalette = LocalQuotaPalette.current
    val effectiveTheme = if (Color(hostPalette.background).luminance() < 0.35f) {
        ThemeMode.DARK
    } else {
        ThemeMode.LIGHT
    }
    val palette = settingsPalette(hostPalette, effectiveTheme)

    Dialog(
        onDismissRequest = if (downloading) ({}) else onLater,
        properties = DialogProperties(
            dismissOnBackPress = !downloading,
            dismissOnClickOutside = !downloading,
            usePlatformDefaultWidth = false,
        ),
    ) {
        CodexQuotaTheme(palette) {
            UpdateLiquidDialogSurface {
                Column(
                            modifier = Modifier.padding(horizontal = 20.dp),
                            verticalArrangement = Arrangement.spacedBy(3.dp),
                        ) {
                            Text(
                                text = "发现新版本",
                                color = palette.color(palette.title),
                                fontSize = 22.sp,
                                fontWeight = FontWeight.SemiBold,
                            )
                            Text(
                                text = release.version.toString(),
                                color = palette.color(palette.title),
                                fontSize = 30.sp,
                                fontWeight = FontWeight.Bold,
                            )
                            Text(
                                text = "当前版本 $currentVersion",
                                color = palette.color(palette.secondary),
                                fontSize = 13.sp,
                            )
                }

                downloadError?.let { error ->
                    Text(
                                text = "下载失败：$error",
                                color = palette.color(palette.error),
                                fontSize = 13.sp,
                                modifier = Modifier.padding(horizontal = 20.dp),
                    )
                }

                if (release.notes.isNotBlank()) {
                    Column(Modifier.padding(horizontal = 20.dp)) {
                                SettingsGroup {
                                    Box(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .heightIn(max = 260.dp)
                                            .verticalScroll(rememberScrollState())
                                            .padding(
                                                horizontal = SettingsUiTokens.rowHorizontalPadding,
                                                vertical = 16.dp,
                                            ),
                                    ) {
                                        ReleaseNotesMarkdownView(release.notes)
                                    }
                                }
                        }
                    }

                if (downloading) {
                    Column(Modifier.padding(horizontal = 20.dp)) {
                                SettingsSection("下载") {
                                    SettingsGroup {
                                        Column(
                                            modifier = Modifier.padding(
                                                horizontal = SettingsUiTokens.rowHorizontalPadding,
                                                vertical = 14.dp,
                                            ),
                                            verticalArrangement = Arrangement.spacedBy(6.dp),
                                        ) {
                                            Text(
                                                text = if (progress.phase == UpdateDownloadPhase.VERIFYING) {
                                                    "正在校验安装包…"
                                                } else {
                                                    "正在下载 ${release.version}"
                                                },
                                                color = palette.color(palette.body),
                                                fontSize = 14.sp,
                                                fontWeight = FontWeight.Medium,
                                            )
                                            Text(
                                                text = UpdateDownloadFormatting.formatProgress(progress),
                                                color = palette.color(palette.secondary),
                                                fontSize = 13.sp,
                                            )
                                            val percentage = progress.percentage
                                            if (percentage == null) {
                                                CircularProgressIndicator(
                                                    modifier = Modifier.size(20.dp),
                                                    strokeWidth = 2.dp,
                                                    color = palette.color(palette.accent),
                                                )
                                            } else {
                                                LinearProgressIndicator(
                                                    progress = { percentage / 100f },
                                                    modifier = Modifier
                                                        .fillMaxWidth()
                                                        .height(8.dp)
                                                        .clip(RoundedCornerShape(4.dp)),
                                                    color = palette.color(palette.accent),
                                                    trackColor = palette.color(palette.progressTrack),
                                                )
                                                Text(
                                                    text = "$percentage%",
                                                    color = palette.color(palette.secondary),
                                                    fontSize = 12.sp,
                                                )
                                            }
                                        }
                                    }
                                }
                        }
                    }

                Column(Modifier.padding(horizontal = 20.dp)) {
                            if (!downloading) {
                                SettingsActionButton(
                                    label = if (downloadError == null) "下载并安装" else "重试",
                                    primary = true,
                                    horizontalInset = 0.dp,
                                    topPadding = 0.dp,
                                    bottomPadding = 0.dp,
                                    onClick = onDownload,
                                )
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(top = 8.dp),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                ) {
                                    TextButton(
                                        onClick = rememberSystemHapticClick(onBrowserDownload),
                                        contentPadding = PaddingValues(horizontal = 0.dp),
                                    ) {
                                        Text(
                                            text = "浏览器下载",
                                            color = palette.color(palette.accent),
                                            fontSize = 13.sp,
                                        )
                                    }
                                    TextButton(
                                        onClick = rememberSystemHapticClick(onLater),
                                        contentPadding = PaddingValues(horizontal = 0.dp),
                                    ) {
                                        Text(
                                            text = "稍后",
                                            color = palette.color(palette.muted),
                                            fontSize = 13.sp,
                                        )
                                    }
                                }
                            } else {
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(top = 4.dp),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                ) {
                                    TextButton(
                                        onClick = rememberSystemHapticClick(onBrowserDownload),
                                        contentPadding = PaddingValues(horizontal = 0.dp),
                                    ) {
                                        Text(
                                            text = "浏览器下载",
                                            color = palette.color(palette.accent),
                                            fontSize = 13.sp,
                                        )
                                    }
                                    TextButton(
                                        onClick = rememberSystemHapticClick(onCancel),
                                        contentPadding = PaddingValues(horizontal = 0.dp),
                                    ) {
                                        Text(
                                            text = "取消",
                                            color = palette.color(palette.muted),
                                            fontSize = 13.sp,
                                        )
                                    }
                                }
                            }
                }
            }
        }
    }
}

@Composable
private fun UpdateLiquidDialogSurface(
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val isDark = palette.color(palette.background).luminance() < 0.35f
    val shape = RoundedCornerShape(SettingsUiTokens.groupCornerRadius)
    val dialogBackdrop = rememberLayerBackdrop()
    Box(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp),
    ) {
        Box(
            Modifier
                .matchParentSize()
                .layerBackdrop(dialogBackdrop)
                .background(palette.color(palette.background)),
        )
        GlassSurface(
            backdrop = dialogBackdrop,
            shape = shape,
            modifier = Modifier.fillMaxWidth(),
            clippedModifier = Modifier.border(
                width = 1.dp,
                color = palette.color(palette.border).copy(alpha = 0.8f),
                shape = shape,
            ),
            contentAlignment = Alignment.TopStart,
            blurRadius = 8.dp,
            refractionHeight = 12.dp,
            refractionAmount = 24.dp,
            surfaceAlpha = if (isDark) 0.46f else 0.58f,
            surfaceColor = palette.color(palette.background),
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 20.dp),
                verticalArrangement = Arrangement.spacedBy(18.dp),
                content = content,
            )
        }
    }
}
