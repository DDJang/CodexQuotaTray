package com.codexquotatray.android

import android.Manifest
import android.app.NotificationManager
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.platform.LocalLocale
import androidx.compose.ui.unit.dp
import com.codexquotatray.android.alerts.QuotaAlertSettingsStore
import com.codexquotatray.android.alerts.QuotaNotifications
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaRefreshSettings
import com.codexquotatray.android.quota.QuotaRefreshSettingsStore
import com.codexquotatray.android.quota.QuotaSnapshotStore
import com.codexquotatray.android.quota.ResetCreditExpiryReminderScheduler
import com.codexquotatray.android.source.AndroidDataSourcePriorityStore
import com.codexquotatray.android.source.DataSourcePriority
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenUsageRefreshSettingsStore
import com.codexquotatray.android.usage.TokenUsageRefreshSettings
import com.codexquotatray.android.usage.TokenUsageRefreshScheduler
import com.codexquotatray.android.usage.TokenUsageCache
import com.codexquotatray.android.usage.TokenUsagePairingLifecycle
import com.codexquotatray.android.usage.tokenUsageSyncErrorMessage
import com.codexquotatray.android.update.SkipReason
import com.codexquotatray.android.update.UpdateCheckReason
import com.codexquotatray.android.update.UpdateCheckResult
import com.codexquotatray.android.update.UpdateInstaller
import com.codexquotatray.android.update.UpdateBrowser
import com.codexquotatray.android.update.UpdateDownloadCancelledException
import com.codexquotatray.android.update.UpdateDownloadProgress
import com.codexquotatray.android.update.UpdateRelease
import com.codexquotatray.android.update.UpdateSettings
import com.codexquotatray.android.update.UpdateSettingsStore
import com.codexquotatray.android.update.UpdateSource
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.Executors

private enum class SettingsDestination(val title: String) {
    ROOT("设置"),
    NOTIFICATIONS("通知"),
    SYNC("数据"),
    THEME("显示与主题"),
    TOKEN_PAIRING("Windows 配对"),
    UPDATE("更新设置"),
}

private const val DEBUG_QUOTA_RING_FIXTURE_ACTIVITY =
    "com.codexquotatray.android.debug.QuotaRingFixtureActivity"

internal fun sourcePriorityOptions(): List<SettingsSegmentOption> = listOf(
    SettingsSegmentOption(0, "OpenAI 优先"),
    SettingsSegmentOption(1, "Windows 优先"),
)

internal fun sourcePriorityValue(priority: DataSourcePriority): Int =
    if (priority == DataSourcePriority.OPENAI_FIRST) 0 else 1

internal fun sourcePriorityFromValue(value: Int): DataSourcePriority =
    if (value == 0) DataSourcePriority.OPENAI_FIRST else DataSourcePriority.WINDOWS_FIRST

class SettingsActivity : ComponentActivity() {
    private val alertStore by lazy { QuotaAlertSettingsStore(this) }
    private val refreshStore by lazy { QuotaRefreshSettingsStore(this) }
    private val themeStore by lazy { ThemeSettingsStore(this) }
    private val tokenStore by lazy { TokenSyncStore(this) }
    private val tokenRefreshStore by lazy { TokenUsageRefreshSettingsStore(this) }
    private val updateSettingsStore by lazy { UpdateSettingsStore(this) }
    private val sourcePriorityStore by lazy { AndroidDataSourcePriorityStore(this) }
    private val pairingWorker = Executors.newSingleThreadExecutor()
    private val pairingMain = android.os.Handler(android.os.Looper.getMainLooper())

    private var destination by mutableStateOf(SettingsDestination.ROOT)
    private var lowQuota by mutableStateOf(false)
    private var resetAlert by mutableStateOf(false)
    private var resetCreditExpiryEnabled by mutableStateOf(false)
    private var resetCreditExpiryLeadHours by mutableStateOf(24)
    private var notificationEnabled by mutableStateOf(false)
    private var quotaAutoRefresh by mutableStateOf(true)
    private var backgroundRefresh by mutableStateOf(false)
    private var refreshInterval by mutableStateOf(QuotaRefreshSettings.DEFAULT_INTERVAL_MINUTES)
    private var tokenAutoSync by mutableStateOf(true)
    private var tokenBackgroundSync by mutableStateOf(false)
    private var tokenSyncInterval by mutableStateOf(TokenUsageRefreshSettings.DEFAULT_INTERVAL_MINUTES)
    private var quotaSourcePriority by mutableStateOf(DataSourcePriority.OPENAI_FIRST)
    private var tokenSourcePriority by mutableStateOf(DataSourcePriority.WINDOWS_FIRST)
    private var themeMode by mutableStateOf(ThemeMode.SYSTEM)
    private var systemThemeVersion by mutableStateOf(0)
    private var pairing by mutableStateOf<TokenSyncPairing?>(null)
    private var tokenStatus by mutableStateOf("尚未配对 Windows")
    private var showClearPairingDialog by mutableStateOf(false)
    private var updateSource by mutableStateOf(UpdateSource.GITHUB)
    private var automaticUpdateChecks by mutableStateOf(true)
    private var updateReminders by mutableStateOf(true)
    private var updateLastCheckAtMillis by mutableStateOf(0L)
    private var updateStatus by mutableStateOf("尚未检查")
    private var updateInfo by mutableStateOf<UpdateRelease?>(null)
    private var updateDialogVisible by mutableStateOf(false)
    private var updateChecking by mutableStateOf(false)
    private var updateDownloading by mutableStateOf(false)
    private var updateProgress by mutableStateOf(UpdateDownloadProgress.Idle)
    private var updateDownloadError by mutableStateOf<String?>(null)
    private var pendingBrowserDownloadUrl: String? = null
    private var pendingInstall by mutableStateOf<java.io.File?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        destination = savedInstanceState?.getString(STATE_DESTINATION)
            ?.let { saved -> SettingsDestination.entries.firstOrNull { it.name == saved } }
            ?: intent.getStringExtra(EXTRA_DESTINATION)
            ?.let { saved -> SettingsDestination.entries.firstOrNull { it.name == saved } }
            ?: SettingsDestination.ROOT
        AppTheme.applySystemBars(this)
        renderState()
        setContent {
            systemThemeVersion
            // Keep the outer visual tree subscribed to the selection. Previously
            // only the child theme rows observed themeMode, so returning to the
            // root could show the new label with the old palette.
            val effectiveTheme = AppTheme.effectiveMode(this, themeMode)
            val palette = rememberAnimatedThemePalette(
                settingsPalette(AppTheme.palette(this, themeMode), effectiveTheme),
            )
            CodexQuotaTheme(palette) {
                val backdrop = rememberLayerBackdrop()
                val scrollState = rememberScrollState()
                var upwardOverscrollActive by remember { mutableStateOf(false) }
                val backgroundColor = palette.color(palette.background)
                Box(Modifier.fillMaxSize()) {
                    Box(
                        Modifier
                            .fillMaxSize()
                            .layerBackdrop(backdrop)
                            .background(backgroundColor),
                    )
                    SettingsContent(
                        page = destination,
                        scrollState = scrollState,
                        onUpwardOverscrollChanged = { upwardOverscrollActive = it },
                        modifier = Modifier.fillMaxSize(),
                    )
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
                            destination.title,
                            Modifier.weight(1f),
                            color = palette.color(palette.title),
                            style = CodexTypography.title,
                            textAlign = TextAlign.Center,
                        )
                        Spacer(Modifier.size(52.dp))
                    }
                }
                if (showClearPairingDialog) {
                    CodexConfirmDialog(
                        title = "解除配对",
                        message = "确定解除当前 Windows 配对吗？",
                        confirmText = "解除",
                        onConfirm = ::clearPairing,
                        onDismiss = { showClearPairingDialog = false },
                    )
                }
                if (updateDialogVisible) {
                    updateInfo?.let { release ->
                        UpdateAvailableDialog(
                            release = release,
                            currentVersion = BuildConfig.VERSION_NAME,
                            downloading = updateDownloading,
                            progress = updateProgress,
                            downloadError = updateDownloadError,
                            onLater = { updateDownloadError = null; updateDialogVisible = false },
                            onDownload = ::downloadAndInstallUpdate,
                            onCancel = { (application as CodexQuotaApplication).updateDownloadManager.cancel() },
                            onBrowserDownload = ::browserDownloadUpdate,
                        )
                    }
                }
            }
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putString(STATE_DESTINATION, destination.name)
        super.onSaveInstanceState(outState)
    }

    override fun onResume() {
        super.onResume()
        renderState()
        tryInstallPendingUpdate()
        if (themeMode == ThemeMode.SYSTEM) {
            themeStore.synchronizeLaunchTheme()
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

    override fun onDestroy() {
        pairingWorker.shutdownNow()
        super.onDestroy()
    }

    @Composable
    private fun SettingsContent(
        page: SettingsDestination,
        scrollState: androidx.compose.foundation.ScrollState,
        onUpwardOverscrollChanged: (Boolean) -> Unit,
        modifier: Modifier = Modifier,
    ) {
        Column(
            modifier
                .dampedVerticalOverscroll(onUpwardOverscrollChanged)
                .verticalScroll(scrollState, overscrollEffect = null)
                .statusBarsPadding()
                .navigationBarsPadding()
                .padding(start = 20.dp, end = 20.dp, top = 86.dp, bottom = 32.dp),
            verticalArrangement = Arrangement.spacedBy(28.dp),
        ) {
            when (page) {
                SettingsDestination.ROOT -> SettingsHome()
                SettingsDestination.NOTIFICATIONS -> NotificationSettings()
                SettingsDestination.SYNC -> SyncSettings()
                SettingsDestination.THEME -> ThemeSettings()
                SettingsDestination.TOKEN_PAIRING -> TokenPairingSettings()
                SettingsDestination.UPDATE -> UpdateSettingsPage()
            }
        }
    }

    @Composable
    private fun ColumnScope.SettingsHome() {
        SettingsSection("账号与配对") {
            SettingsGroup {
                SettingsNavigationRow("Codex 额度账号") {
                    startActivity(Intent(this@SettingsActivity, AccountActivity::class.java))
                }
                SettingsDivider()
                SettingsNavigationRow(
                    title = "Windows 配对",
                    trailing = pairing?.displayName ?: "未配对",
                ) { openDestination(SettingsDestination.TOKEN_PAIRING) }
            }
        }
        SettingsSection("通知与数据") {
            SettingsGroup {
                if (!backgroundRefresh) {
                    SettingsWarningCaption("未开启额度后台刷新时，通知可能会延迟")
                }
                SettingsNavigationRow("通知", if (notificationEnabled) "已开启" else "未开启") {
                    openDestination(SettingsDestination.NOTIFICATIONS)
                }
                SettingsDivider()
                SettingsNavigationRow("数据", if (backgroundRefresh || tokenBackgroundSync) "已开启" else "已关闭") {
                    openDestination(SettingsDestination.SYNC)
                }
            }
        }
        SettingsSection("个性化") {
            SettingsGroup {
                SettingsNavigationRow("主题", themeLabel(themeMode)) {
                    openDestination(SettingsDestination.THEME)
                }
            }
        }
        SettingsSection("其他") {
            SettingsGroup {
                SettingsNavigationRow("运行日志") {
                    startActivity(Intent(this@SettingsActivity, LogActivity::class.java))
                }
                SettingsDivider()
                SettingsNavigationRow("更新设置") {
                    openDestination(SettingsDestination.UPDATE)
                }
                SettingsDivider()
                SettingsNavigationRow("关于") {
                    startActivity(Intent(this@SettingsActivity, AboutActivity::class.java))
                }
            }
        }
        if (BuildConfig.DEBUG) {
            SettingsSection("开发者选项") {
                SettingsGroup {
                    SettingsNavigationRow(
                        title = "Quota Ring Fixture",
                        trailing = "Debug",
                        onClick = ::openDebugQuotaRingFixture,
                    )
                }
            }
        }
    }

    private fun openDebugQuotaRingFixture() {
        if (BuildConfig.DEBUG) {
            startActivity(
                Intent().setClassName(
                    this,
                    DEBUG_QUOTA_RING_FIXTURE_ACTIVITY,
                ),
            )
        }
    }

    @Composable
    private fun ColumnScope.NotificationSettings() {
        SettingsSection("系统通知") {
            SettingsGroup {
                SettingsToggleRow("系统通知", notificationEnabled) {
                    if (it) requestNotificationPermission() else openNotificationSettings()
                }
            }
        }
        SettingsSection("额度提醒") {
            SettingsGroup {
                SettingsToggleRow("低额度提醒", lowQuota, enabled = notificationEnabled) {
                    lowQuota = it
                    alertStore.save(alertStore.load().copy(lowQuotaEnabled = it))
                    AppLogStore.record(this@SettingsActivity, "低额度提醒已${if (it) "开启" else "关闭"}")
                }
                SettingsDivider()
                SettingsToggleRow("额度重置提醒", resetAlert, enabled = notificationEnabled) {
                    resetAlert = it
                    alertStore.save(alertStore.load().copy(resetEnabled = it))
                    AppLogStore.record(this@SettingsActivity, "额度重置提醒已${if (it) "开启" else "关闭"}")
                }
            }
        }
        SettingsSection(stringResource(R.string.reset_credit_expiry_section)) {
            SettingsGroup {
                SettingsToggleRow(
                    title = stringResource(R.string.reset_credit_expiry_toggle),
                    checked = resetCreditExpiryEnabled,
                    enabled = notificationEnabled,
                    onChange = ::updateResetCreditExpiry,
                )
                SettingsDivider()
                SettingsInlineLabel(
                    stringResource(R.string.reset_credit_expiry_lead),
                    enabled = notificationEnabled && resetCreditExpiryEnabled,
                )
                SettingsSegmentedSelector(
                    options = listOf(
                        SettingsSegmentOption(24, stringResource(R.string.reset_credit_expiry_lead_day)),
                        SettingsSegmentOption(6, stringResource(R.string.reset_credit_expiry_lead_six_hours)),
                        SettingsSegmentOption(1, stringResource(R.string.reset_credit_expiry_lead_one_hour)),
                    ),
                    selectedValue = resetCreditExpiryLeadHours,
                    enabled = notificationEnabled && resetCreditExpiryEnabled,
                    onSelected = ::selectResetCreditExpiryLead,
                )
            }
        }
    }

    @Composable
    private fun ColumnScope.SyncSettings() {
        SettingsSection("额度") {
            SettingsGroup {
                SettingsInlineLabel("数据来源")
                SettingsSegmentedSelector(
                    options = sourcePriorityOptions(),
                    selectedValue = sourcePriorityValue(quotaSourcePriority),
                    enabled = true,
                    onSelected = { selectQuotaSourcePriority(sourcePriorityFromValue(it)) },
                )
                SettingsDivider()
                SettingsToggleRow(
                    "回到前台时刷新",
                    quotaAutoRefresh,
                    onChange = ::updateQuotaAutoRefresh,
                )
                SettingsDivider()
                SettingsToggleRow(
                    "后台自动刷新",
                    backgroundRefresh,
                    onChange = ::updateBackgroundRefresh,
                )
                SettingsDivider()
                SettingsInlineLabel("刷新频率", enabled = backgroundRefresh)
                SettingsSegmentedSelector(
                    options = QuotaRefreshSettings.SUPPORTED_INTERVAL_MINUTES.map { minutes ->
                        SettingsSegmentOption(
                            value = minutes,
                            label = if (minutes < 60) "$minutes 分" else "1 小时",
                        )
                    },
                    selectedValue = refreshInterval,
                    enabled = backgroundRefresh,
                    onSelected = ::selectRefreshInterval,
                )
            }
        }
        SettingsSection("统计") {
            SettingsGroup {
                SettingsInlineLabel("数据来源")
                SettingsSegmentedSelector(
                    options = sourcePriorityOptions(),
                    selectedValue = sourcePriorityValue(tokenSourcePriority),
                    enabled = true,
                    onSelected = { selectTokenSourcePriority(sourcePriorityFromValue(it)) },
                )
                SettingsDivider()
                SettingsToggleRow(
                    "回到前台时同步",
                    tokenAutoSync,
                    onChange = ::updateTokenAutoSync,
                )
                SettingsDivider()
                SettingsToggleRow(
                    "后台自动同步",
                    tokenBackgroundSync,
                    onChange = ::updateTokenBackgroundSync,
                )
                SettingsDivider()
                SettingsInlineLabel("同步频率", enabled = tokenBackgroundSync)
                SettingsSegmentedSelector(
                    options = TokenUsageRefreshSettings.SUPPORTED_INTERVAL_MINUTES.map { minutes ->
                        SettingsSegmentOption(
                            value = minutes,
                            label = if (minutes < 60) "$minutes 分" else "1 小时",
                        )
                    },
                    selectedValue = tokenSyncInterval,
                    enabled = tokenBackgroundSync,
                    onSelected = ::selectTokenSyncInterval,
                )
            }
        }
        SettingsSection("电池优化") {
            SettingsGroup {
                SettingsNavigationRow("电池优化", onClick = ::openBatterySettings)
            }
        }
    }

    @Composable
    private fun ColumnScope.ThemeSettings() {
        SettingsSection("外观") {
            SettingsGroup {
                SettingsSelectionRow("跟随系统", themeMode == ThemeMode.SYSTEM) { selectTheme(ThemeMode.SYSTEM) }
                SettingsDivider()
                SettingsSelectionRow("浅色模式", themeMode == ThemeMode.LIGHT) { selectTheme(ThemeMode.LIGHT) }
                SettingsDivider()
                SettingsSelectionRow("深色模式", themeMode == ThemeMode.DARK) { selectTheme(ThemeMode.DARK) }
            }
        }
    }

    @Composable
    private fun ColumnScope.TokenPairingSettings() {
        val locale = LocalLocale.current.platformLocale
        SettingsSection("Windows") {
            SettingsGroup {
                SettingsInfoRow("状态", tokenStatus)
                pairing?.let {
                    SettingsDivider()
                    SettingsInfoRow("电脑", "${it.displayName ?: "Windows PC"} · ${it.host}:${it.port}")
                    SettingsInfoRow(
                        "上次同步",
                        it.lastSyncUtc?.let { raw -> formatPairingTime(raw, locale) } ?: "尚未成功同步",
                    )
                    SettingsActionButton("立即同步", onClick = ::syncPairedNow)
                    SettingsActionButton("重新扫码配对", onClick = ::scanPairing)
                    SettingsActionButton(
                        label = "解除配对",
                        danger = true,
                        bottomPadding = SettingsUiTokens.actionEdgeInset,
                    ) { showClearPairingDialog = true }
                } ?: run {
                    SettingsActionButton(
                        label = "扫码配对",
                        bottomPadding = SettingsUiTokens.actionEdgeInset,
                        onClick = ::scanPairing,
                    )
                }
            }
        }
    }

    private fun openDestination(value: SettingsDestination) {
        startActivity(
            Intent(this, SettingsActivity::class.java)
                .putExtra(EXTRA_DESTINATION, value.name),
        )
    }

    private fun renderState() {
        val alert = alertStore.load()
        val refresh = refreshStore.load()
        lowQuota = alert.lowQuotaEnabled
        resetAlert = alert.resetEnabled
        resetCreditExpiryEnabled = alert.resetCreditExpiryEnabled
        resetCreditExpiryLeadHours = alert.resetCreditExpiryLeadHours
        notificationEnabled = notificationsEnabled()
        quotaAutoRefresh = refresh.autoRefreshOnOpen
        backgroundRefresh = refresh.enabled
        refreshInterval = refresh.normalizedIntervalMinutes
        tokenRefreshStore.load().also { tokenRefresh ->
            tokenAutoSync = tokenRefresh.autoSyncOnOpen
            tokenBackgroundSync = tokenRefresh.backgroundSyncEnabled
            tokenSyncInterval = tokenRefresh.normalizedIntervalMinutes
        }
        sourcePriorityStore.load().also { priorities ->
            quotaSourcePriority = priorities.quota
            tokenSourcePriority = priorities.token
        }
        themeMode = themeStore.load()
        pairing = tokenStore.load()
        tokenStatus = if (pairing == null) "尚未配对 Windows" else "已配对"
        updateSettingsStore.load().also { update ->
            updateSource = update.source
            automaticUpdateChecks = update.automaticChecksEnabled
            updateReminders = update.updateRemindersEnabled
            updateLastCheckAtMillis = update.lastCheckAtMillis
        }
    }

    private fun updateQuotaAutoRefresh(enabled: Boolean) {
        quotaAutoRefresh = enabled
        refreshStore.save(refreshStore.load().copy(autoRefreshOnOpen = enabled))
        AppLogStore.record(this, "回到前台时刷新已${if (enabled) "开启" else "关闭"}")
    }

    private fun updateBackgroundRefresh(enabled: Boolean) {
        backgroundRefresh = enabled
        refreshStore.save(refreshStore.load().copy(enabled = enabled))
        QuotaRefreshScheduler.schedule(this)
        AppLogStore.record(this, "额度后台自动刷新已${if (enabled) "开启" else "关闭"}")
    }

    private fun updateResetCreditExpiry(enabled: Boolean) {
        resetCreditExpiryEnabled = enabled
        alertStore.save(
            alertStore.load().copy(resetCreditExpiryEnabled = enabled),
        )
        ResetCreditExpiryReminderScheduler.evaluateNow(this)
        ResetCreditExpiryReminderScheduler.schedule(this)
        AppLogStore.record(this, "重置卡临期提醒已${if (enabled) "开启" else "关闭"}")
    }

    private fun selectResetCreditExpiryLead(hours: Int) {
        resetCreditExpiryLeadHours = when (hours) {
            6 -> 6
            1 -> 1
            else -> 24
        }
        alertStore.save(
            alertStore.load().copy(resetCreditExpiryLeadHours = resetCreditExpiryLeadHours),
        )
        ResetCreditExpiryReminderScheduler.evaluateNow(this)
        ResetCreditExpiryReminderScheduler.schedule(this)
        AppLogStore.record(this, "重置卡临期提醒提前时间设为 ${resetCreditExpiryLeadHours} 小时")
    }

    private fun updateTokenAutoSync(enabled: Boolean) {
        tokenAutoSync = enabled
        tokenRefreshStore.save(tokenRefreshStore.load().copy(autoSyncOnOpen = enabled))
        AppLogStore.record(this, "回到前台时 Token 自动同步已${if (enabled) "开启" else "关闭"}")
    }

    private fun updateTokenBackgroundSync(enabled: Boolean) {
        tokenBackgroundSync = enabled
        tokenRefreshStore.save(tokenRefreshStore.load().copy(backgroundSyncEnabled = enabled))
        TokenUsageRefreshScheduler.schedule(this)
        AppLogStore.record(this, "Token 后台自动同步已${if (enabled) "开启" else "关闭"}")
    }

    private fun selectRefreshInterval(minutes: Int) {
        refreshInterval = minutes
        refreshStore.save(refreshStore.load().copy(intervalMinutes = minutes))
        QuotaRefreshScheduler.schedule(this)
        AppLogStore.record(this, "额度后台刷新频率设为 $minutes 分钟")
    }

    private fun selectTokenSyncInterval(minutes: Int) {
        tokenSyncInterval = minutes
        tokenRefreshStore.save(tokenRefreshStore.load().copy(intervalMinutes = minutes))
        TokenUsageRefreshScheduler.schedule(this)
        AppLogStore.record(this, "Token 后台同步频率设为 $minutes 分钟")
    }

    private fun selectQuotaSourcePriority(priority: DataSourcePriority) {
        quotaSourcePriority = priority
        sourcePriorityStore.save(sourcePriorityStore.load().copy(quota = priority))
        AppLogStore.record(this, "额度数据来源优先级已更新")
    }

    private fun selectTokenSourcePriority(priority: DataSourcePriority) {
        tokenSourcePriority = priority
        sourcePriorityStore.save(sourcePriorityStore.load().copy(token = priority))
        AppLogStore.record(this, "Token 数据来源优先级已更新")
    }

    private fun selectTheme(mode: ThemeMode) {
        if (themeMode == mode) return
        themeStore.save(mode)
        themeMode = mode
        AppTheme.applySystemBars(this)
        AppLogStore.record(this, "主题已切换")
    }

    private fun themeLabel(mode: ThemeMode): String = when (mode) {
        ThemeMode.SYSTEM -> "跟随系统"
        ThemeMode.LIGHT -> "浅色模式"
        ThemeMode.DARK -> "深色模式"
    }

    private fun requestNotificationPermission() {
        QuotaNotifications.ensureChannel(this)
        if (!notificationsEnabled()) {
            openNotificationSettings()
        }
        notificationEnabled = notificationsEnabled()
    }

    private fun openNotificationSettings() {
        runCatching {
            startActivity(
                Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                    .putExtra(Settings.EXTRA_APP_PACKAGE, packageName),
            )
        }.onFailure { AppLogStore.record(this, "打开系统通知设置失败", "WARN") }
    }

    private fun openBatterySettings() {
        runCatching {
            startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            AppLogStore.record(this, "打开电池优化设置")
        }.onFailure { Toast.makeText(this, "无法打开系统电池设置", Toast.LENGTH_SHORT).show() }
    }

    private fun notificationsEnabled(): Boolean {
        val permission = checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        return permission && (getSystemService(NotificationManager::class.java)?.areNotificationsEnabled() ?: true)
    }

    private fun savePairing(result: Result<TokenSyncPairing>) {
        result.onSuccess { value ->
            if (TokenPairingFlow.savePairing(this, value)) {
                renderState()
                Toast.makeText(this, "Token 同步配对已保存", Toast.LENGTH_SHORT).show()
                testPairing(value)
            } else {
                Toast.makeText(this, "无法安全保存配对信息", Toast.LENGTH_SHORT).show()
            }
        }.onFailure { Toast.makeText(this, it.message ?: "配对信息无效", Toast.LENGTH_SHORT).show() }
    }

    private fun scanPairing() = TokenPairingFlow.launchScan(this)

    @Suppress("DEPRECATION")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        when (val scan = TokenPairingFlow.parseScanResult(requestCode, resultCode, data)) {
            null -> super.onActivityResult(requestCode, resultCode, data)
            TokenPairingScanResult.Cancelled -> Toast.makeText(this, "未读取二维码", Toast.LENGTH_SHORT).show()
            is TokenPairingScanResult.Pairing -> savePairing(scan.result)
        }
    }

    @Composable
    private fun ColumnScope.UpdateSettingsPage() {
        val locale = LocalLocale.current.platformLocale
        SettingsSection("下载源") {
            SettingsGroup {
                SettingsSelectionRow("GitHub", updateSource == UpdateSource.GITHUB) {
                    updateSource = UpdateSource.GITHUB
                    saveUpdateSettings()
                }
                SettingsDivider()
                SettingsNavigationRow(
                    title = "Gitee",
                    trailing = "暂不可用",
                    enabled = false,
                ) { }
            }
        }
        SettingsSection("更新行为") {
            SettingsGroup {
                SettingsToggleRow(
                    "自动检查更新",
                    automaticUpdateChecks,
                    onChange = ::updateAutomaticChecks,
                )
                SettingsDivider()
                SettingsToggleRow(
                    "更新提醒",
                    updateReminders,
                    enabled = automaticUpdateChecks,
                    onChange = ::updateReminderSetting,
                )
            }
        }
        SettingsSection("版本") {
            SettingsGroup {
                SettingsInfoRow("当前版本", BuildConfig.VERSION_NAME)
                SettingsDivider()
                SettingsInfoRow("上次检查", formatUpdateCheckTime(updateLastCheckAtMillis, locale))
                SettingsDivider()
                SettingsInfoRow("状态", updateStatus)
                SettingsActionButton(
                    label = if (updateChecking) "正在检查…" else "检查更新",
                    enabled = !updateChecking,
                    bottomPadding = SettingsUiTokens.actionEdgeInset,
                    onClick = ::checkForUpdates,
                )
            }
        }
    }

    private fun testPairing(value: TokenSyncPairing) {
        tokenStatus = "正在测试 Windows 连接…"
        TokenPairingFlow.testPairing(this, value, pairingWorker, pairingMain) { result ->
            result.onSuccess {
                renderState()
                Toast.makeText(this, "Windows 配对成功", Toast.LENGTH_SHORT).show()
            }.onFailure { error ->
                renderState()
                Toast.makeText(
                    this,
                    "已保存配对；${tokenUsageSyncErrorMessage(error)}",
                    Toast.LENGTH_LONG,
                ).show()
            }
        }
    }

    private fun syncPairedNow() {
        tokenStore.load()?.let(::testPairing)
            ?: Toast.makeText(this, "尚未配对 Windows", Toast.LENGTH_SHORT).show()
    }

    private fun clearPairing() {
        val cleared = TokenUsagePairingLifecycle.withLock {
            TokenUsagePairingLifecycle.clear(tokenStore, TokenUsageCache(this)).also { success ->
                if (success) QuotaSnapshotStore(this).invalidateWindowsForPairing(null)
            }
        }
        if (cleared) {
            com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(this)
            TokenUsageRefreshScheduler.cancel(this)
            QuotaRefreshScheduler.schedule(this)
            renderState()
            Toast.makeText(this, "Windows 配对已解除", Toast.LENGTH_SHORT).show()
        } else {
            Toast.makeText(this, "无法解除 Windows 配对", Toast.LENGTH_SHORT).show()
        }
    }

    private fun updateAutomaticChecks(enabled: Boolean) {
        automaticUpdateChecks = enabled
        saveUpdateSettings()
        AppLogStore.record(this, "自动检查更新已${if (enabled) "开启" else "关闭"}")
    }

    private fun updateReminderSetting(enabled: Boolean) {
        updateReminders = enabled
        saveUpdateSettings()
        AppLogStore.record(this, "更新提醒已${if (enabled) "开启" else "关闭"}")
    }

    private fun saveUpdateSettings() {
        val previous = updateSettingsStore.load()
        updateSettingsStore.save(
            UpdateSettings(
                source = updateSource,
                automaticChecksEnabled = automaticUpdateChecks,
                updateRemindersEnabled = updateReminders,
                lastCheckAtMillis = updateLastCheckAtMillis,
                lastNotifiedVersion = previous.lastNotifiedVersion,
            ),
        )
    }

    private fun checkForUpdates() {
        if (updateChecking) return
        updateChecking = true
        updateStatus = "正在检查…"
        (application as CodexQuotaApplication).updateCheckCoordinator.check(UpdateCheckReason.MANUAL) { result ->
            updateChecking = false
            updateLastCheckAtMillis = updateSettingsStore.load().lastCheckAtMillis
            when (result) {
                is UpdateCheckResult.Available -> {
                    updateInfo = result.release
                    updateStatus = "发现新版本 ${result.release.version}"
                    updateDialogVisible = true
                }
                is UpdateCheckResult.UpToDate -> {
                    updateInfo = null
                    updateStatus = "已是最新版本 ${result.currentVersion}"
                }
                is UpdateCheckResult.NoAndroidAsset -> {
                    updateInfo = null
                    updateStatus = "当前 Release 没有 Android 安装包"
                }
                is UpdateCheckResult.Failed -> {
                    updateInfo = null
                    updateStatus = "检查更新失败：${result.message}"
                }
                is UpdateCheckResult.Skipped -> {
                    updateInfo = null
                    updateStatus = when (result.reason) {
                        SkipReason.SOURCE_UNAVAILABLE -> "Gitee 更新源暂不可用"
                        SkipReason.AUTO_DISABLED -> "自动检查更新已关闭"
                        SkipReason.WITHIN_INTERVAL -> "自动检查仍在 24 小时限制内"
                        SkipReason.IN_FLIGHT -> "检查更新正在进行中"
                    }
                }
            }
        }
    }

    private fun downloadAndInstallUpdate() {
        val asset = updateInfo?.androidAsset ?: return
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
                        updateDialogVisible = false
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

    private fun browserDownloadUpdate() {
        val url = updateInfo?.androidAsset?.browserDownloadUrl ?: return
        if (updateDownloading) {
            pendingBrowserDownloadUrl = url
            if (!(application as CodexQuotaApplication).updateDownloadManager.cancel()) {
                // The worker may have finished just before the UI callback ran.
                // Keep the request pending so the callback discards the APK
                // before opening the browser.
            }
            return
        }
        openBrowserDownload(url)
    }

    private fun openBrowserDownload(url: String) {
        runCatching { UpdateBrowser.open(this, url) }
            .onSuccess { updateDialogVisible = false }
            .onFailure { error -> updateStatus = error.message ?: "无法打开浏览器下载" }
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
                    updateStatus = "已打开系统安装器"
                } else {
                    updateStatus = "请允许安装未知应用后返回继续"
                }
            }
            .onFailure { error -> updateStatus = error.message ?: "无法打开系统安装器" }
    }

    private fun formatUpdateCheckTime(value: Long, locale: java.util.Locale): String = if (value <= 0L) {
        "尚未检查"
    } else {
        DateTimeFormatter.ofPattern("MM-dd HH:mm", locale)
            .withZone(ZoneId.systemDefault())
            .format(Instant.ofEpochMilli(value))
    }

    private fun formatPairingTime(raw: String, locale: java.util.Locale): String = runCatching {
        DateTimeFormatter.ofPattern("MM-dd HH:mm", locale)
            .withZone(ZoneId.systemDefault())
            .format(Instant.parse(raw))
    }.getOrDefault("未知")

    companion object {
        private const val STATE_DESTINATION = "settings_destination"
        private const val EXTRA_DESTINATION = "settings_destination_extra"
    }
}
