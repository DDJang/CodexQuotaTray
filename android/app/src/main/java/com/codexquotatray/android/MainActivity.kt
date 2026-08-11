package com.codexquotatray.android

import android.content.Intent
import android.os.Bundle
import android.content.res.Configuration
import android.os.Handler
import android.os.Looper
import android.widget.Toast
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
import androidx.compose.runtime.mutableStateOf
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
import java.util.concurrent.Executors

class MainActivity : ComponentActivity() {
    private lateinit var quota: QuotaPageController
    private lateinit var usage: TokenUsagePageController
    private var selectedIndex by mutableIntStateOf(0)
    private var themeMode by mutableStateOf(ThemeMode.SYSTEM)
    private var systemThemeVersion by mutableIntStateOf(0)
    private var foregroundRegistration: AutoCloseable? = null
    private val pairingWorker = Executors.newSingleThreadExecutor()
    private val pairingMain = Handler(Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        themeMode = AppTheme.mode(this)
        AppLogStore.record(this, "应用启动")
        quota = QuotaPageController(this)
        usage = TokenUsagePageController(this)
        quota.initialize()
        TokenUsageRefreshScheduler.schedule(this)
        foregroundRegistration = (application as CodexQuotaApplication).registerForegroundListener { reason ->
            quota.onForeground(reason)
            usage.onForeground(reason)
        }
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (selectedIndex == 1) selectTab(0) else { isEnabled = false; onBackPressedDispatcher.onBackPressed() }
            }
        })
        setContent {
            systemThemeVersion
            val palette = rememberAnimatedThemePalette(AppTheme.palette(this, themeMode))
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
                            if (selectedIndex == 0) QuotaPage(quota) else TokenUsagePage(usage, ::scanTokenPairing)
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
        if (::usage.isInitialized) usage.reconcilePairingState()
        val restoredTheme = AppTheme.mode(this)
        if (themeMode != restoredTheme) {
            themeMode = restoredTheme
        }
        if (restoredTheme == ThemeMode.SYSTEM) {
            ThemeSettingsStore(this).synchronizeLaunchTheme()
            systemThemeVersion++
        }
        AppTheme.applySystemBars(this)
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (AppTheme.mode(this) == ThemeMode.SYSTEM) {
            systemThemeVersion++
        }
        AppTheme.applySystemBars(this)
    }

    @Suppress("DEPRECATION")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        when (val scan = TokenPairingFlow.parseScanResult(requestCode, resultCode, data)) {
            null -> if (::quota.isInitialized) quota.onLoginResult(requestCode, resultCode)
            TokenPairingScanResult.Cancelled -> Toast.makeText(this, "未读取二维码", Toast.LENGTH_SHORT).show()
            is TokenPairingScanResult.Pairing -> saveTokenPairing(scan.result)
        }
    }

    override fun onDestroy() {
        foregroundRegistration?.close()
        foregroundRegistration = null
        if (::quota.isInitialized) quota.destroy()
        if (::usage.isInitialized) usage.destroy()
        pairingWorker.shutdownNow()
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

    private fun scanTokenPairing() = TokenPairingFlow.launchScan(this)

    private fun saveTokenPairing(result: Result<com.codexquotatray.android.usage.TokenSyncPairing>) {
        result.onSuccess { pairing ->
            if (!TokenPairingFlow.savePairing(this, pairing)) {
                Toast.makeText(this, "无法安全保存配对信息", Toast.LENGTH_SHORT).show()
                return@onSuccess
            }
            usage.reconcilePairingState()
            Toast.makeText(this, "Token 同步配对已保存", Toast.LENGTH_SHORT).show()
            TokenPairingFlow.testPairing(this, pairing, pairingWorker, pairingMain) { syncResult ->
                usage.reconcilePairingState()
                syncResult.onSuccess {
                    Toast.makeText(this, "Windows 配对成功", Toast.LENGTH_SHORT).show()
                }.onFailure { error ->
                    Toast.makeText(this, "已保存配对；${com.codexquotatray.android.usage.tokenUsageSyncErrorMessage(error)}", Toast.LENGTH_LONG).show()
                }
            }
        }.onFailure {
            Toast.makeText(this, it.message ?: "配对信息无效", Toast.LENGTH_SHORT).show()
        }
    }
}
