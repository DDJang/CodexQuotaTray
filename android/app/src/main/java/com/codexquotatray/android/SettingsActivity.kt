package com.codexquotatray.android

import android.Manifest
import android.app.NotificationManager
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.alerts.QuotaAlertSettingsStore
import com.codexquotatray.android.alerts.QuotaNotifications
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaRefreshSettings
import com.codexquotatray.android.quota.QuotaRefreshSettingsStore
import com.codexquotatray.android.usage.TokenSyncEndpoint
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenUsageException
import com.codexquotatray.android.usage.TokenUsageRefreshSettingsStore
import com.codexquotatray.android.usage.TokenUsageSyncClient
import com.google.zxing.integration.android.IntentIntegrator
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.Executors

private enum class SettingsDestination(val title: String) {
    ROOT("设置"),
    NOTIFICATIONS("通知"),
    SYNC("同步"),
    THEME("显示与主题"),
    TOKEN_PAIRING("Token 用量账号"),
}

class SettingsActivity : ComponentActivity() {
    private val alertStore by lazy { QuotaAlertSettingsStore(this) }
    private val refreshStore by lazy { QuotaRefreshSettingsStore(this) }
    private val themeStore by lazy { ThemeSettingsStore(this) }
    private val tokenStore by lazy { TokenSyncStore(this) }
    private val tokenRefreshStore by lazy { TokenUsageRefreshSettingsStore(this) }
    private val pairingWorker = Executors.newSingleThreadExecutor()
    private val pairingMain = android.os.Handler(android.os.Looper.getMainLooper())

    private var destination by mutableStateOf(SettingsDestination.ROOT)
    private var lowQuota by mutableStateOf(false)
    private var resetAlert by mutableStateOf(false)
    private var notificationEnabled by mutableStateOf(false)
    private var backgroundRefresh by mutableStateOf(false)
    private var refreshInterval by mutableStateOf(QuotaRefreshSettings.DEFAULT_INTERVAL_MINUTES)
    private var tokenAutoSync by mutableStateOf(true)
    private var themeMode by mutableStateOf(ThemeMode.SYSTEM)
    private var pairing by mutableStateOf<TokenSyncPairing?>(null)
    private var tokenStatus by mutableStateOf("尚未配对 Windows")
    private var pairingUri by mutableStateOf("")
    private var pairingHost by mutableStateOf("")
    private var pairingSecret by mutableStateOf("")
    private var showClearPairingDialog by mutableStateOf(false)

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
            val palette = settingsPalette(AppTheme.palette(this))
            CodexQuotaTheme(palette) {
                val backdrop = rememberLayerBackdrop()
                val scrollState = rememberScrollState()
                val backgroundColor = palette.color(palette.background)
                Box(Modifier.fillMaxSize().background(backgroundColor)) {
                    Box(Modifier.fillMaxSize().layerBackdrop(backdrop)) {
                        Box(Modifier.fillMaxSize().background(backgroundColor)) {
                            SettingsContent(
                                page = destination,
                                scrollState = scrollState,
                                modifier = Modifier.fillMaxSize(),
                            )
                        }
                    }
                    SettingsGradientBlurHeader(
                        backdrop = backdrop,
                        scrollState = scrollState,
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
                            size = 52.dp,
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
                        message = "确定解除当前 Windows Token Usage 配对吗？",
                        confirmText = "解除",
                        onConfirm = ::clearPairing,
                        onDismiss = { showClearPairingDialog = false },
                    )
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
    }

    override fun onDestroy() {
        pairingWorker.shutdownNow()
        super.onDestroy()
    }

    @Composable
    private fun SettingsContent(
        page: SettingsDestination,
        scrollState: androidx.compose.foundation.ScrollState,
        modifier: Modifier = Modifier,
    ) {
        Column(
            modifier
                .dampedVerticalOverscroll()
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
            }
        }
    }

    @Composable
    private fun ColumnScope.SettingsHome() {
        SettingsSection("账号") {
            NavigationRow("Codex 额度账号") {
                startActivity(Intent(this@SettingsActivity, AccountActivity::class.java))
            }
            SettingsDivider()
            NavigationRow(
                title = "Token 用量账号",
                trailing = pairing?.displayName ?: "未配对",
            ) { openDestination(SettingsDestination.TOKEN_PAIRING) }
        }
        SettingsSection("通知与同步") {
            if (!backgroundRefresh) {
                Text(
                    "未开启同步时，通知可能会延迟",
                    color = CodexColors.danger,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                )
                SettingsDivider()
            }
            NavigationRow("通知", if (notificationEnabled) "已开启" else "未开启") {
                openDestination(SettingsDestination.NOTIFICATIONS)
            }
            SettingsDivider()
            NavigationRow("同步", if (backgroundRefresh) "已开启" else "已关闭") {
                openDestination(SettingsDestination.SYNC)
            }
        }
        SettingsSection("个性化") {
            NavigationRow("主题", themeLabel(themeMode)) {
                openDestination(SettingsDestination.THEME)
            }
        }
        SettingsSection("其他") {
            NavigationRow("运行日志") {
                startActivity(Intent(this@SettingsActivity, LogActivity::class.java))
            }
            SettingsDivider()
            NavigationRow("关于") {
                startActivity(Intent(this@SettingsActivity, AboutActivity::class.java))
            }
        }
    }

    @Composable
    private fun ColumnScope.NotificationSettings() {
        SettingsSection("系统通知") {
            ToggleRow("系统通知", notificationEnabled) {
                if (it) requestNotificationPermission() else openNotificationSettings()
            }
            SettingsButton("发送测试通知", enabled = notificationEnabled, onClick = ::sendTestNotification)
        }
        SettingsSection("额度提醒") {
            ToggleRow("低额度提醒", lowQuota, enabled = notificationEnabled) {
                lowQuota = it
                alertStore.save(alertStore.load().copy(lowQuotaEnabled = it))
                AppLogStore.record(this@SettingsActivity, "低额度提醒已${if (it) "开启" else "关闭"}")
            }
            SettingsDivider()
            ToggleRow("额度重置提醒", resetAlert, enabled = notificationEnabled) {
                resetAlert = it
                alertStore.save(alertStore.load().copy(resetEnabled = it))
                AppLogStore.record(this@SettingsActivity, "额度重置提醒已${if (it) "开启" else "关闭"}")
            }
        }
    }

    @Composable
    private fun ColumnScope.SyncSettings() {
        SettingsSection("剩余额度") {
            ToggleRow("后台自动刷新", backgroundRefresh, onChange = ::updateBackgroundRefresh)
            SettingsDivider()
            Text(
                "刷新频率",
                fontWeight = FontWeight.Bold,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 10.dp)
                    .alpha(if (backgroundRefresh) 1f else 0.45f),
            )
            RefreshIntervalButtons(enabled = backgroundRefresh)
            SettingsDivider()
            NavigationRow("电池优化", enabled = backgroundRefresh, onClick = ::openBatterySettings)
        }
        SettingsSection("Token 使用量") {
            ToggleRow("打开统计页时自动同步", tokenAutoSync, onChange = ::updateTokenAutoSync)
        }
    }

    @Composable
    private fun ColumnScope.ThemeSettings() {
        SettingsSection("外观") {
            SelectionRow("跟随系统", themeMode == ThemeMode.SYSTEM) { selectTheme(ThemeMode.SYSTEM) }
            SettingsDivider()
            SelectionRow("浅色模式", themeMode == ThemeMode.LIGHT) { selectTheme(ThemeMode.LIGHT) }
            SettingsDivider()
            SelectionRow("深色模式", themeMode == ThemeMode.DARK) { selectTheme(ThemeMode.DARK) }
        }
    }

    @Composable
    private fun ColumnScope.TokenPairingSettings() {
        SettingsSection("Windows") {
            Text(
                tokenStatus,
                color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondary),
                fontSize = 15.sp,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 10.dp),
            )
            pairing?.let {
                SettingsDivider()
                Text(
                    "${it.displayName ?: "Windows PC"} · ${it.host}:${it.port}",
                    color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondary),
                    fontSize = 14.sp,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 10.dp),
                )
                Text(
                    it.lastSyncUtc?.let { raw -> "上次同步 ${formatPairingTime(raw)}" } ?: "尚未成功同步",
                    color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondary),
                    fontSize = 14.sp,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                )
            }
            if (pairing == null) {
                SettingsButton("扫码配对", onClick = ::scanPairing)
                OutlinedTextField(
                    pairingUri,
                    { pairingUri = it },
                    Modifier.fillMaxWidth().padding(horizontal = 12.dp),
                    label = { Text("粘贴 codexquota://pair?…") },
                    singleLine = true,
                )
                SettingsButton("保存粘贴的配对信息") {
                    savePairing(runCatching { TokenSyncEndpoint.parsePairingUri(pairingUri) })
                }
                OutlinedTextField(
                    pairingHost,
                    { pairingHost = it },
                    Modifier.fillMaxWidth().padding(horizontal = 12.dp),
                    label = { Text("Windows 地址") },
                    singleLine = true,
                )
                OutlinedTextField(
                    pairingSecret,
                    { pairingSecret = it },
                    Modifier.fillMaxWidth().padding(horizontal = 12.dp),
                    label = { Text("配对密钥") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                )
                SettingsButton("保存手动配置") {
                    savePairing(runCatching { TokenSyncEndpoint.parseManual(pairingHost, pairingSecret) })
                }
            } else {
                SettingsButton("立即同步", onClick = ::syncPairedNow)
                SettingsButton("重新扫码配对", onClick = ::scanPairing)
                SettingsButton("解除配对", danger = true) { showClearPairingDialog = true }
            }
        }
    }

    @Composable
    private fun SettingsSection(title: String, content: @Composable ColumnScope.() -> Unit) {
        val palette = LocalQuotaPalette.current
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(
                title,
                color = palette.color(palette.secondary),
                fontSize = 14.sp,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.padding(start = 8.dp),
            )
            Card(
                Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(CodexDimensions.cardRadius),
                colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface)),
            ) {
                Column(Modifier.fillMaxWidth().padding(vertical = 5.dp), content = content)
            }
        }
    }

    @Composable
    private fun SettingsDivider() {
        val palette = LocalQuotaPalette.current
        HorizontalDivider(
            modifier = Modifier.padding(horizontal = 18.dp),
            thickness = 0.5.dp,
            color = palette.color(palette.border),
        )
    }

    @Composable
    private fun ToggleRow(
        title: String,
        checked: Boolean,
        enabled: Boolean = true,
        onChange: (Boolean) -> Unit,
    ) {
        val hapticOnChange = rememberSystemHapticChange(onChange)
        Row(
            Modifier
                .fillMaxWidth()
                .clickable(enabled = enabled) { hapticOnChange(!checked) }
                .alpha(if (enabled) 1f else 0.45f)
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(title, Modifier.weight(1f), fontSize = 16.sp, fontWeight = FontWeight.Bold)
            Switch(
                checked = checked,
                onCheckedChange = if (enabled) hapticOnChange else null,
                enabled = enabled,
                colors = SwitchDefaults.colors(
                    checkedThumbColor = Color.White,
                    checkedTrackColor = LocalQuotaPalette.current.color(LocalQuotaPalette.current.accent),
                    uncheckedThumbColor = Color(0xFFF1F1F1),
                    uncheckedTrackColor = Color(0xFF4A4A4A),
                    uncheckedBorderColor = Color.Transparent,
                ),
            )
        }
    }

    @Composable
    private fun NavigationRow(
        title: String,
        trailing: String? = null,
        enabled: Boolean = true,
        onClick: () -> Unit,
    ) {
        val hapticOnClick = rememberSystemHapticClick(onClick)
        Row(
            Modifier
                .fillMaxWidth()
                .clickable(enabled = enabled, onClick = hapticOnClick)
                .alpha(if (enabled) 1f else 0.45f)
                .padding(horizontal = 16.dp, vertical = 15.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(title, Modifier.weight(1f), fontSize = 16.sp, fontWeight = FontWeight.Bold)
            trailing?.let {
                Text(it, color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondary), fontSize = 14.sp)
                Spacer(Modifier.size(8.dp))
            }
            Text("›", fontSize = 24.sp)
        }
    }

    @Composable
    private fun SelectionRow(title: String, selected: Boolean, onClick: () -> Unit) {
        val hapticOnClick = rememberSystemHapticClick(onClick)
        Row(
            Modifier.fillMaxWidth().clickable(onClick = hapticOnClick)
                .padding(horizontal = 16.dp, vertical = 16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(title, Modifier.weight(1f), fontSize = 17.sp, fontWeight = FontWeight.Bold)
            if (selected) {
                Text("✓", color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.accent), fontSize = 24.sp)
            }
        }
    }

    @Composable
    private fun RefreshIntervalButtons(enabled: Boolean) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            QuotaRefreshSettings.SUPPORTED_INTERVAL_MINUTES.forEach { minutes ->
                val hapticOnClick = rememberSystemHapticClick { selectRefreshInterval(minutes) }
                Button(
                    onClick = hapticOnClick,
                    enabled = enabled,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = if (refreshInterval == minutes) {
                            LocalQuotaPalette.current.color(LocalQuotaPalette.current.primaryButton)
                        } else {
                            LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondaryButton)
                        },
                        contentColor = if (refreshInterval == minutes) Color.White
                        else LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondaryButtonText),
                        disabledContainerColor = LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondaryButton).copy(alpha = 0.45f),
                        disabledContentColor = LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondaryButtonText).copy(alpha = 0.5f),
                    ),
                    shape = RoundedCornerShape(CodexDimensions.buttonRadius),
                    modifier = Modifier.weight(1f).height(CodexDimensions.rowHeight),
                ) {
                    Text(if (minutes < 60) "$minutes 分" else "1 小时", fontSize = 13.sp)
                }
            }
        }
    }

    @Composable
    private fun SettingsButton(
        label: String,
        danger: Boolean = false,
        enabled: Boolean = true,
        onClick: () -> Unit,
    ) {
        CodexButton(
            text = label,
            onClick = onClick,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
            enabled = enabled,
            style = if (danger) CodexButtonStyle.DANGER else CodexButtonStyle.SECONDARY,
        )
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
        notificationEnabled = notificationsEnabled()
        backgroundRefresh = refresh.enabled
        refreshInterval = refresh.normalizedIntervalMinutes
        tokenAutoSync = tokenRefreshStore.load().autoSyncOnOpen
        themeMode = themeStore.load()
        pairing = tokenStore.load()
        tokenStatus = if (pairing == null) "尚未配对 Windows" else "已配对"
        pairingHost = pairing?.let { "${it.host}:${it.port}" } ?: pairingHost
    }

    private fun settingsPalette(base: ThemePalette): ThemePalette =
        if (AppTheme.effectiveMode(this) == ThemeMode.DARK) {
            base.copy(
                background = 0xff000000.toInt(),
                surface = 0xff252525.toInt(),
                border = 0xff343434.toInt(),
                title = 0xfff5f5f5.toInt(),
                body = 0xffeeeeee.toInt(),
                secondary = 0xff969696.toInt(),
                muted = 0xff8d8d8d.toInt(),
                secondaryButton = 0xff333333.toInt(),
                secondaryButtonText = 0xfff2f2f2.toInt(),
                progressTrack = 0xff3a3a3a.toInt(),
            )
        } else {
            base
        }

    private fun updateBackgroundRefresh(enabled: Boolean) {
        backgroundRefresh = enabled
        refreshStore.save(refreshStore.load().copy(enabled = enabled))
        QuotaRefreshScheduler.schedule(this)
        AppLogStore.record(this, "后台自动刷新已${if (enabled) "开启" else "关闭"}")
    }

    private fun updateTokenAutoSync(enabled: Boolean) {
        tokenAutoSync = enabled
        tokenRefreshStore.save(tokenRefreshStore.load().copy(autoSyncOnOpen = enabled))
        AppLogStore.record(this, "Token 使用量自动同步已${if (enabled) "开启" else "关闭"}")
    }

    private fun selectRefreshInterval(minutes: Int) {
        refreshInterval = minutes
        refreshStore.save(refreshStore.load().copy(intervalMinutes = minutes))
        QuotaRefreshScheduler.schedule(this)
        AppLogStore.record(this, "后台刷新频率设为 $minutes 分钟")
    }

    private fun selectTheme(mode: ThemeMode) {
        if (themeStore.load() == mode) return
        themeStore.save(mode)
        themeMode = mode
        AppLogStore.record(this, "主题已切换")
        recreate()
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

    private fun sendTestNotification() {
        if (!notificationsEnabled()) return
        if (QuotaNotifications.sendTest(this)) {
            AppLogStore.record(this, "已发送通知测试")
            Toast.makeText(this, "测试通知已发送", Toast.LENGTH_SHORT).show()
        } else {
            AppLogStore.record(this, "通知测试未发送", "WARN")
            Toast.makeText(this, "通知未发送，请检查系统通知权限", Toast.LENGTH_SHORT).show()
        }
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
        val permission = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        return permission && (getSystemService(NotificationManager::class.java)?.areNotificationsEnabled() ?: true)
    }

    private fun savePairing(result: Result<TokenSyncPairing>) {
        result.onSuccess { value ->
            if (tokenStore.save(value)) {
                pairingUri = ""
                pairingSecret = ""
                pairingHost = "${value.host}:${value.port}"
                renderState()
                Toast.makeText(this, "Token 同步配对已保存", Toast.LENGTH_SHORT).show()
                testPairing(value)
            } else {
                Toast.makeText(this, "无法安全保存配对信息", Toast.LENGTH_SHORT).show()
            }
        }.onFailure { Toast.makeText(this, it.message ?: "配对信息无效", Toast.LENGTH_SHORT).show() }
    }

    private fun scanPairing() {
        IntentIntegrator(this)
            .setDesiredBarcodeFormats("QR_CODE")
            .setPrompt("扫描 Windows Token Usage 配对二维码")
            .setBeepEnabled(false)
            .setOrientationLocked(false)
            .initiateScan()
    }

    @Suppress("DEPRECATION")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        val result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data)
        if (result != null) {
            if (!result.contents.isNullOrBlank()) {
                savePairing(runCatching { TokenSyncEndpoint.parsePairingUri(result.contents) })
            } else {
                Toast.makeText(this, "未读取二维码，可继续手动输入配对信息", Toast.LENGTH_SHORT).show()
            }
            return
        }
        super.onActivityResult(requestCode, resultCode, data)
    }

    private fun testPairing(value: TokenSyncPairing) {
        tokenStatus = "正在测试 Windows 连接…"
        pairingWorker.execute {
            val result = runCatching { TokenUsageSyncClient(this).sync(value) }
            pairingMain.post {
                result.onSuccess { synced ->
                    tokenStore.save(TokenSyncEndpoint.markSynced(synced.pairing, synced.snapshot))
                    renderState()
                    Toast.makeText(this, "Windows 配对成功", Toast.LENGTH_SHORT).show()
                }.onFailure { error ->
                    renderState()
                    Toast.makeText(
                        this,
                        "已保存配对；${(error as? TokenUsageException)?.message ?: "Windows 当前不可用"}",
                        Toast.LENGTH_LONG,
                    ).show()
                }
            }
        }
    }

    private fun syncPairedNow() {
        tokenStore.load()?.let(::testPairing)
            ?: Toast.makeText(this, "尚未配对 Windows", Toast.LENGTH_SHORT).show()
    }

    private fun clearPairing() {
        if (tokenStore.clear()) {
            renderState()
            Toast.makeText(this, "Windows 配对已解除", Toast.LENGTH_SHORT).show()
        } else {
            Toast.makeText(this, "无法解除 Windows 配对", Toast.LENGTH_SHORT).show()
        }
    }

    private fun formatPairingTime(raw: String): String = runCatching {
        DateTimeFormatter.ofPattern("MM-dd HH:mm", java.util.Locale.getDefault())
            .withZone(ZoneId.systemDefault())
            .format(Instant.parse(raw))
    }.getOrDefault("未知")

    companion object {
        private const val STATE_DESTINATION = "settings_destination"
        private const val EXTRA_DESTINATION = "settings_destination_extra"
    }
}
