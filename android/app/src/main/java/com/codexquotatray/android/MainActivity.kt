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
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.animation.core.tween
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
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.drawscope.ContentDrawScope
import androidx.compose.ui.graphics.layer.drawLayer
import androidx.compose.ui.graphics.rememberGraphicsLayer
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.usage.TokenUsageRefreshScheduler
import com.codexquotatray.android.update.UpdateInstaller
import com.codexquotatray.android.update.UpdateBrowser
import com.codexquotatray.android.update.UpdateDownloadCancelledException
import com.codexquotatray.android.update.UpdateDownloadProgress
import com.codexquotatray.android.update.UpdateRelease
import com.codexquotatray.android.widget.QuotaWidgetBridge
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import java.util.concurrent.Executors

internal const val ACTION_OPEN_FROM_WIDGET = "com.codexquotatray.android.action.OPEN_FROM_WIDGET"

class MainActivity : ComponentActivity() {
    private lateinit var quota: QuotaPageController
    private lateinit var usage: TokenUsagePageController
    private var selectedIndex by mutableIntStateOf(0)
    private var themeMode by mutableStateOf(ThemeMode.SYSTEM)
    private var systemThemeVersion by mutableIntStateOf(0)
    private var foregroundRegistration: AutoCloseable? = null
    private var updateReminderRegistration: AutoCloseable? = null
    private var updatePrompt by mutableStateOf<UpdateRelease?>(null)
    private var updateDownloading by mutableStateOf(false)
    private var updateProgress by mutableStateOf(UpdateDownloadProgress.Idle)
    private var updateDownloadError by mutableStateOf<String?>(null)
    private var pendingBrowserDownloadUrl: String? = null
    private var pendingInstall by mutableStateOf<java.io.File?>(null)
    private val pairingWorker = Executors.newSingleThreadExecutor()
    private val pairingMain = Handler(Looper.getMainLooper())
    private var lanRecoveryRegistration: AutoCloseable? = null
    private var activityStarted = false

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
        val app = application as CodexQuotaApplication
        updateReminderRegistration = app.registerUpdateReminderListener { release ->
            updatePrompt = release
        }
        foregroundRegistration = app.registerForegroundListener { reason ->
            quota.onForeground(reason)
            usage.onForeground(reason)
            app.updateCheckCoordinator.requestAutomaticCheck { result ->
                if (result is com.codexquotatray.android.update.UpdateCheckResult.Failed) {
                    AppLogStore.record(this, "自动检查更新失败", "WARN")
                }
            }
        }
        lanRecoveryRegistration = app.lanNetworkLifecycle.addStableListener {
            if (activityStarted && !isFinishing && !isDestroyed) {
                quota.onNetworkRestored()
                usage.onNetworkRestored()
            }
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
                val sceneLayer = rememberGraphicsLayer()
                val drawSceneLayer: ContentDrawScope.() -> Unit = remember(sceneLayer) {
                    { drawLayer(sceneLayer) }
                }
                val chromeBackdrop = rememberLayerBackdrop(onDraw = drawSceneLayer)
                Box(Modifier.fillMaxSize()) {
                    Box(
                        Modifier
                            .fillMaxSize()
                            .layerBackdrop(chromeBackdrop)
                            .drawWithContent {
                                val content = this
                                sceneLayer.record {
                                    content.drawContent()
                                }
                                drawLayer(sceneLayer)
                            },
                    ) {
                        Box(
                            Modifier
                                .fillMaxSize()
                                .background(palette.color(palette.background)),
                        )
                        Column(
                            Modifier
                                .fillMaxSize()
                                .statusBarsPadding(),
                        ) {
                            Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 72.dp, top = 14.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                                Text(
                                    text = if (selectedIndex == 0) "额度" else "统计",
                                    color = palette.color(palette.title),
                                    fontSize = 28.sp,
                                    fontWeight = FontWeight.Bold,
                                )
                            }
                            Box(Modifier.weight(1f)) {
                                AnimatedContent(
                                    targetState = selectedIndex,
                                    modifier = Modifier.fillMaxSize(),
                                    transitionSpec = {
                                        val direction = if (targetState > initialState) 1 else -1
                                        (
                                            fadeIn(animationSpec = tween(200)) +
                                                slideInHorizontally(
                                                    animationSpec = tween(200),
                                                    initialOffsetX = { width -> direction * width / 20 },
                                                )
                                            ) togetherWith (
                                            fadeOut(animationSpec = tween(160)) +
                                                slideOutHorizontally(
                                                    animationSpec = tween(160),
                                                    targetOffsetX = { width -> -direction * width / 28 },
                                                )
                                            )
                                    },
                                    label = "main-page-transition",
                                ) { pageIndex ->
                                    if (pageIndex == 0) {
                                        QuotaPage(quota, ::scanTokenPairing)
                                    } else {
                                        TokenUsagePage(usage, ::scanTokenPairing, quota::openLogin)
                                    }
                                }
                            }
                        }
                    }
                    Box(Modifier.align(Alignment.TopEnd).statusBarsPadding().padding(top = 8.dp, end = 20.dp)) {
                        LiquidIconButton(
                            iconRes = R.drawable.ic_settings,
                            description = "设置",
                            backdrop = chromeBackdrop,
                            buttonSize = 48.dp,
                            iconSize = 24.dp,
                            onClick = ::openSettings,
                        )
                    }
                    LiquidMainDock(
                        selectedIndex = selectedIndex,
                        onSelected = ::selectTab,
                        backdrop = chromeBackdrop,
                        actionEnabled = if (selectedIndex == 0) quota.canRefresh else usage.canSync,
                        actionBusy = if (selectedIndex == 0) quota.busy else usage.syncing,
                        actionDescription = if (selectedIndex == 0) "刷新额度" else "同步统计",
                        onAction = { if (selectedIndex == 0) quota.refresh() else usage.requestSync() },
                        modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(horizontal = 18.dp, vertical = 12.dp).fillMaxWidth(),
                    )
                    updatePrompt?.let { release ->
                        UpdateAvailableDialog(
                            backdrop = chromeBackdrop,
                            release = release,
                            currentVersion = BuildConfig.VERSION_NAME,
                            downloading = updateDownloading,
                            progress = updateProgress,
                            downloadError = updateDownloadError,
                            onLater = { updateDownloadError = null; updatePrompt = null },
                            onDownload = ::downloadAutomaticUpdate,
                            onCancel = { (application as CodexQuotaApplication).updateDownloadManager.cancel() },
                            onBrowserDownload = ::browserDownloadAutomaticUpdate,
                        )
                    }
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        if (intent.action == ACTION_OPEN_FROM_WIDGET) {
            selectTab(0)
        }
    }

    override fun onStart() {
        super.onStart()
        activityStarted = true
        if (::quota.isInitialized) quota.onStart()
        if (::usage.isInitialized) usage.onStart()
    }
    override fun onStop() {
        activityStarted = false
        if (::quota.isInitialized) quota.onStop()
        if (::usage.isInitialized) usage.onStop()
        super.onStop()
    }
    override fun onResume() {
        super.onResume()
        QuotaWidgetBridge.syncFromCurrentMainSnapshot(this)
        tryInstallPendingUpdate()
        if (::quota.isInitialized && selectedIndex == 0) quota.onVisible()
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
        lanRecoveryRegistration?.close()
        lanRecoveryRegistration = null
        foregroundRegistration?.close()
        foregroundRegistration = null
        updateReminderRegistration?.close()
        updateReminderRegistration = null
        if (::quota.isInitialized) quota.destroy()
        if (::usage.isInitialized) usage.destroy()
        pairingWorker.shutdownNow()
        super.onDestroy()
    }

    private fun selectTab(index: Int) {
        val targetIndex = index.coerceIn(0, 1)
        if (targetIndex == selectedIndex) return
        selectedIndex = targetIndex
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
            if (selectedIndex == 0) quota.onVisible()
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

    private fun downloadAutomaticUpdate() {
        val asset = updatePrompt?.androidAsset ?: return
        if (updateDownloading) return
        updateDownloading = true
        updateDownloadError = null
        updateProgress = UpdateDownloadProgress(
            phase = com.codexquotatray.android.update.UpdateDownloadPhase.DOWNLOADING,
            totalBytes = null,
        )
        (application as CodexQuotaApplication).updateDownloadManager.download(
            asset = asset,
            onProgress = { progress ->
                runOnUiThread { updateProgress = progress }
            },
        ) { result ->
            runOnUiThread {
                updateDownloading = false
                val browserUrl = pendingBrowserDownloadUrl
                pendingBrowserDownloadUrl = null
                if (browserUrl != null) {
                    result.getOrNull()?.delete()
                    openBrowserDownload(browserUrl)
                } else {
                    result.onSuccess { apk ->
                        pendingInstall = apk
                        updatePrompt = null
                        launchPendingInstall()
                    }.onFailure { error ->
                        if (error !is UpdateDownloadCancelledException) {
                            updateDownloadError = error.message ?: "无法下载更新安装包。"
                        }
                    }
                }
            }
        }
    }

    private fun browserDownloadAutomaticUpdate() {
        val url = updatePrompt?.androidAsset?.browserDownloadUrl ?: return
        if (updateDownloading) {
            pendingBrowserDownloadUrl = url
            if (!(application as CodexQuotaApplication).updateDownloadManager.cancel()) {
                // The worker may have finished just before the UI callback ran.
                // Keep the request pending; the callback will discard the APK and
                // open the browser instead of launching the installer.
            }
            return
        }
        openBrowserDownload(url)
    }

    private fun openBrowserDownload(url: String) {
        runCatching { UpdateBrowser.open(this, url) }
            .onSuccess { updatePrompt = null }
            .onFailure { error ->
                Toast.makeText(this, error.message ?: "无法打开浏览器下载", Toast.LENGTH_LONG).show()
            }
    }

    private fun tryInstallPendingUpdate() {
        if (pendingInstall?.isFile == true && UpdateInstaller.canRequestPackageInstalls(this)) {
            launchPendingInstall()
        }
    }

    private fun launchPendingInstall() {
        val apk = pendingInstall ?: return
        runCatching { UpdateInstaller.install(this, apk) }
            .onSuccess { result ->
                if (result == com.codexquotatray.android.update.InstallUpdateResult.STARTED) {
                    pendingInstall = null
                }
            }
    }
}
