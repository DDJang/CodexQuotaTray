package com.codexquotatray.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.content.res.Configuration
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

class AboutActivity : ComponentActivity() {
    private var themeVersion by mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContent {
            themeVersion
            val effectiveTheme = AppTheme.effectiveMode(this)
            val palette = settingsPalette(AppTheme.palette(this), effectiveTheme)
            val backgroundColor = palette.color(palette.background)
            val backdrop = rememberLayerBackdrop()
            val scrollState = rememberScrollState()
            var upwardOverscrollActive by remember { mutableStateOf(false) }
            CodexQuotaTheme(palette) {
                Box(Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize().layerBackdrop(backdrop)) {
                        AboutAmbientBackground(
                            dark = effectiveTheme == ThemeMode.DARK,
                            modifier = Modifier.fillMaxSize(),
                        )
                        Column(
                            Modifier
                                .fillMaxSize()
                                .dampedVerticalOverscroll { upwardOverscrollActive = it }
                                .verticalScroll(scrollState, overscrollEffect = null)
                                .statusBarsPadding()
                                .navigationBarsPadding()
                                .padding(start = 20.dp, end = 20.dp, top = 86.dp, bottom = 32.dp),
                        ) {
                            Column(
                                Modifier.fillMaxWidth(),
                                horizontalAlignment = Alignment.CenterHorizontally,
                            ) {
                                Image(
                                    painter = painterResource(R.drawable.ic_launcher_mark),
                                    contentDescription = "CodexQuota 图标",
                                    modifier = Modifier.size(112.dp),
                                )
                                Text(
                                    "CodexQuota",
                                    modifier = Modifier.padding(top = 22.dp),
                                    color = palette.color(palette.title),
                                    fontSize = 23.sp,
                                    fontWeight = FontWeight.Bold,
                                )
                                Text(
                                    "版本 ${installedVersion()}",
                                    modifier = Modifier.padding(top = 8.dp),
                                    color = palette.color(palette.muted),
                                    fontSize = 14.sp,
                                )
                                Text(
                                    PROJECT_URL,
                                    modifier = Modifier.padding(top = 18.dp).clickable(onClick = rememberSystemHapticClick(::openProjectPage)),
                                    color = palette.color(palette.accent),
                                    fontSize = 14.sp,
                                )
                            }
                        }
                    }
                    SettingsGradientBlurHeader(
                        backdrop = backdrop,
                        scrollState = scrollState,
                        isScrolled = scrollState.value > 0 || upwardOverscrollActive,
                        tint = backgroundColor,
                        modifier = Modifier.align(Alignment.TopCenter),
                    )
                    Row(
                        Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 18.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        GlassIconButton(
                            iconRes = R.drawable.ic_back,
                            description = "返回",
                            backdrop = backdrop,
                            buttonSize = 52.dp,
                            iconSize = 25.dp,
                            onClick = ::finish,
                        )
                        Text(
                            "关于",
                            Modifier.weight(1f),
                            style = CodexTypography.title,
                            color = palette.color(palette.title),
                            textAlign = TextAlign.Center,
                        )
                        Spacer(Modifier.size(52.dp))
                    }
                }
            }
        }
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (AppTheme.mode(this) == ThemeMode.SYSTEM) themeVersion++
        AppTheme.applySystemBars(this)
    }

    @Suppress("DEPRECATION")
    private fun installedVersion(): String =
        packageManager.getPackageInfo(packageName, 0).versionName ?: "未知"

    private fun openProjectPage() {
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(PROJECT_URL))) }
    }

    companion object {
        private const val PROJECT_URL = "https://github.com/DDJang/CodexQuotaTray"
    }
}
