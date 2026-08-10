package com.codexquotatray.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.usage.TokenUsageRefreshScheduler
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

class MainActivity : ComponentActivity() {
    private lateinit var quota: QuotaPageController
    private lateinit var usage: TokenUsagePageController
    private var selectedIndex by mutableIntStateOf(0)
    private var appliedTheme: ThemeMode? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        appliedTheme = AppTheme.effectiveMode(this)
        AppLogStore.record(this, "应用启动")
        quota = QuotaPageController(this)
        usage = TokenUsagePageController(this)
        quota.initialize()
        TokenUsageRefreshScheduler.schedule(this)
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (selectedIndex == 1) selectTab(0) else { isEnabled = false; onBackPressedDispatcher.onBackPressed() }
            }
        })
        setContent {
            val palette = AppTheme.palette(this)
            CodexQuotaTheme(palette) {
                val pageBackdrop = rememberLayerBackdrop()
                Box(Modifier.fillMaxSize().background(palette.color(palette.background))) {
                    Column(
                        Modifier.fillMaxSize().layerBackdrop(pageBackdrop).statusBarsPadding(),
                    ) {
                        Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 72.dp, top = 14.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text("CodexQuota", color = palette.color(palette.title), fontSize = 28.sp, fontWeight = FontWeight.Bold)
                        }
                        Box(Modifier.weight(1f)) {
                            if (selectedIndex == 0) QuotaPage(quota) else TokenUsagePage(usage, ::openSettings)
                        }
                    }
                    Box(Modifier.align(Alignment.TopEnd).statusBarsPadding().padding(top = 8.dp, end = 20.dp)) {
                        GlassIconButton(
                            iconRes = R.drawable.ic_settings,
                            description = "设置",
                            backdrop = pageBackdrop,
                            size = 52.dp,
                            iconSize = 24.dp,
                            onClick = ::openSettings,
                        )
                    }
                    LiquidMainDock(
                        selectedIndex = selectedIndex,
                        onSelected = ::selectTab,
                        backdrop = pageBackdrop,
                        actionEnabled = if (selectedIndex == 0) quota.canRefresh else usage.canSync,
                        actionBusy = if (selectedIndex == 0) quota.busy else usage.syncing,
                        onAction = { if (selectedIndex == 0) quota.refresh() else usage.requestSync() },
                        modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(horizontal = 18.dp, vertical = 12.dp).fillMaxWidth(),
                    )
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        if (::quota.isInitialized) quota.onStart()
        if (::usage.isInitialized) usage.onStart()
    }
    override fun onStop() {
        if (::quota.isInitialized) quota.onStop()
        if (::usage.isInitialized) usage.onStop()
        super.onStop()
    }
    override fun onResume() {
        super.onResume()
        if (appliedTheme != AppTheme.effectiveMode(this)) { recreate(); return }
        if (::quota.isInitialized) quota.onResume()
        if (selectedIndex == 0 && ::quota.isInitialized) quota.onVisible()
        if (selectedIndex == 1 && ::usage.isInitialized) usage.onResume()
    }

    @Suppress("DEPRECATION")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (::quota.isInitialized) quota.onLoginResult(requestCode, resultCode)
    }

    override fun onDestroy() {
        if (::quota.isInitialized) quota.destroy()
        if (::usage.isInitialized) usage.destroy()
        super.onDestroy()
    }

    private fun selectTab(index: Int) {
        selectedIndex = index.coerceIn(0, 1)
        if (selectedIndex == 1) {
            quota.onHidden()
            usage.onVisible()
        } else {
            usage.onHidden()
            quota.onVisible()
        }
    }

    private fun openSettings() = startActivity(Intent(this, SettingsActivity::class.java))
}
