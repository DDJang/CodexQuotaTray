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
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
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
import com.codexquotatray.android.usage.TokenUsageSyncClient
import com.google.zxing.integration.android.IntentIntegrator
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.Executors

class SettingsActivity : ComponentActivity() {
    private val alertStore by lazy { QuotaAlertSettingsStore(this) }
    private val refreshStore by lazy { QuotaRefreshSettingsStore(this) }
    private val themeStore by lazy { ThemeSettingsStore(this) }
    private val tokenStore by lazy { TokenSyncStore(this) }
    private val pairingWorker = Executors.newSingleThreadExecutor()
    private val pairingMain = android.os.Handler(android.os.Looper.getMainLooper())

    private var lowQuota by mutableStateOf(false)
    private var resetAlert by mutableStateOf(false)
    private var notificationEnabled by mutableStateOf(false)
    private var backgroundRefresh by mutableStateOf(false)
    private var refreshInterval by mutableStateOf(15)
    private var themeMode by mutableStateOf(ThemeMode.SYSTEM)
    private var pairing by mutableStateOf<TokenSyncPairing?>(null)
    private var tokenStatus by mutableStateOf("尚未配对 Windows")
    private var pairingUri by mutableStateOf("")
    private var pairingHost by mutableStateOf("")
    private var pairingSecret by mutableStateOf("")

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        renderState()
        setContent {
            val palette = AppTheme.palette(this)
            CodexQuotaTheme(palette) {
                val backdrop = rememberLayerBackdrop()
                Box(Modifier.fillMaxSize().background(palette.color(palette.background))) {
                    SettingsContent(backdrop = backdrop, modifier = Modifier.fillMaxSize().layerBackdrop(backdrop))
                    Row(
                        Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 16.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        GlassIconButton("‹", "返回", backdrop, onClick = ::finish)
                        Text("设置", Modifier.weight(1f), color = palette.color(palette.title), fontSize = 21.sp, fontWeight = FontWeight.Bold, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                        Spacer(Modifier.height(48.dp).weight(0.12f))
                    }
                }
            }
        }
    }

    override fun onResume() { super.onResume(); renderState() }
    override fun onDestroy() { pairingWorker.shutdownNow(); super.onDestroy() }

    @Composable
    private fun SettingsContent(backdrop: com.kyant.backdrop.Backdrop, modifier: Modifier = Modifier) {
        Column(
            modifier.verticalScroll(rememberScrollState()).statusBarsPadding().navigationBarsPadding().padding(start = 20.dp, end = 20.dp, top = 82.dp, bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(22.dp),
        ) {
            SettingsSection("账户与同步") {
                NavigationRow("账号管理", "查看当前 Codex 登录状态") { startActivity(Intent(this@SettingsActivity, AccountActivity::class.java)) }
                TokenSyncSettings()
            }
            SettingsSection("额度提醒") {
                ToggleRow("低额度提醒", "额度跨过 50%、20% 或 10% 阈值时提醒。", lowQuota) {
                    lowQuota = it; alertStore.save(alertStore.load().copy(lowQuotaEnabled = it)); AppLogStore.record(this@SettingsActivity, "低额度提醒已${if (it) "开启" else "关闭"}")
                }
                ToggleRow("额度重置提醒", "检测到额度窗口重置后提醒。", resetAlert) {
                    resetAlert = it; alertStore.save(alertStore.load().copy(resetEnabled = it)); AppLogStore.record(this@SettingsActivity, "额度重置提醒已${if (it) "开启" else "关闭"}")
                }
            }
            SettingsSection("通知与后台") {
                ToggleRow("系统通知", "由 Android 管理权限；关闭后不会收到额度通知。", notificationEnabled) { if (it) requestNotificationPermission() else openNotificationSettings() }
                Text("当前状态：${if (notificationEnabled) "已开启" else "未开启"}", fontSize = 12.sp, color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.muted), modifier = Modifier.padding(horizontal = 14.dp))
                SettingsButton("发送测试通知", onClick = ::sendTestNotification)
                ToggleRow("后台自动刷新", "按设定频率在网络可用时读取额度。", backgroundRefresh) {
                    backgroundRefresh = it; refreshStore.save(refreshStore.load().copy(enabled = it)); QuotaRefreshScheduler.schedule(this@SettingsActivity); AppLogStore.record(this@SettingsActivity, "后台自动刷新已${if (it) "开启" else "关闭"}")
                }
                Text("刷新频率", fontWeight = FontWeight.Bold, modifier = Modifier.padding(horizontal = 14.dp, vertical = 4.dp))
                Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    QuotaRefreshSettings.SUPPORTED_INTERVAL_MINUTES.forEach { minutes ->
                        Button(
                            onClick = { selectRefreshInterval(minutes) }, enabled = backgroundRefresh,
                            colors = ButtonDefaults.buttonColors(containerColor = if (refreshInterval == minutes) LocalQuotaPalette.current.color(LocalQuotaPalette.current.primaryButton) else LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondaryButton)),
                            modifier = Modifier.weight(1f),
                        ) { Text(if (minutes < 60) "$minutes 分" else "1 小时", fontSize = 12.sp) }
                    }
                }
                Text("Android 可能因省电策略延迟后台任务。", fontSize = 12.sp, color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.muted), modifier = Modifier.padding(horizontal = 14.dp))
                NavigationRow("电池优化", "后台刷新延迟时，可将 CodexQuota 设为不受限制。", ::openBatterySettings)
            }
            SettingsSection("外观主题") {
                Row(Modifier.fillMaxWidth().padding(8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    listOf(ThemeMode.SYSTEM to "系统", ThemeMode.LIGHT to "浅色", ThemeMode.DARK to "暗色").forEach { (mode, label) ->
                        Button(onClick = { selectTheme(mode) }, modifier = Modifier.weight(1f), colors = ButtonDefaults.buttonColors(containerColor = if (themeMode == mode) LocalQuotaPalette.current.color(LocalQuotaPalette.current.primaryButton) else LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondaryButton))) { Text(label) }
                    }
                }
            }
            SettingsSection("诊断与关于") {
                NavigationRow("运行日志", "查看脱敏运行摘要") { startActivity(Intent(this@SettingsActivity, LogActivity::class.java)) }
                NavigationRow("关于", "版本、许可与项目信息") { startActivity(Intent(this@SettingsActivity, AboutActivity::class.java)) }
            }
        }
    }

    @Composable
    private fun SettingsSection(title: String, content: @Composable ColumnScope.() -> Unit) {
        val palette = LocalQuotaPalette.current
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(title, color = palette.color(palette.secondary), fontSize = 13.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(start = 6.dp))
            Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(20.dp), colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface).copy(alpha = 0.88f))) {
                Column(Modifier.fillMaxWidth().padding(vertical = 6.dp), content = content)
            }
        }
    }

    @Composable
    private fun ToggleRow(title: String, subtitle: String, checked: Boolean, onChange: (Boolean) -> Unit) {
        Row(Modifier.fillMaxWidth().clickable { onChange(!checked) }.padding(horizontal = 16.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) { Text(title, fontWeight = FontWeight.Bold); Text(subtitle, fontSize = 12.sp, color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.muted)) }
            Switch(checked, onCheckedChange = onChange)
        }
    }

    @Composable
    private fun NavigationRow(title: String, subtitle: String, onClick: () -> Unit) {
        Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 14.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) { Text(title, fontWeight = FontWeight.Bold); Text(subtitle, fontSize = 12.sp, color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.muted)) }
            Text("›", fontSize = 24.sp)
        }
    }

    @Composable
    private fun SettingsButton(label: String, danger: Boolean = false, onClick: () -> Unit) {
        Button(onClick, Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), colors = ButtonDefaults.buttonColors(containerColor = LocalQuotaPalette.current.color(if (danger) LocalQuotaPalette.current.error else LocalQuotaPalette.current.secondaryButton))) { Text(label) }
    }

    @Composable
    private fun TokenSyncSettings() {
        val palette = LocalQuotaPalette.current
        Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Token 使用量同步", fontWeight = FontWeight.Bold)
            Text(tokenStatus, color = palette.color(palette.muted), fontSize = 14.sp)
            pairing?.let {
                Text("电脑：${it.displayName ?: "Windows PC"} · ${it.host}:${it.port}", color = palette.color(palette.muted), fontSize = 13.sp)
                Text(it.lastSyncUtc?.let { raw -> "上次同步：${formatPairingTime(raw)}" } ?: "上次同步：尚未成功", color = palette.color(palette.muted), fontSize = 13.sp)
            }
            SettingsButton("扫描二维码", onClick = ::scanPairing)
            if (pairing == null) {
                OutlinedTextField(pairingUri, { pairingUri = it }, Modifier.fillMaxWidth(), label = { Text("粘贴 codexquota://pair?…") }, singleLine = true)
                SettingsButton("保存粘贴的配对信息") { savePairing(runCatching { TokenSyncEndpoint.parsePairingUri(pairingUri) }) }
                OutlinedTextField(pairingHost, { pairingHost = it }, Modifier.fillMaxWidth(), label = { Text("Windows 地址，例如 192.168.1.10:43821") }, singleLine = true)
                OutlinedTextField(pairingSecret, { pairingSecret = it }, Modifier.fillMaxWidth(), label = { Text("配对密钥") }, singleLine = true, visualTransformation = PasswordVisualTransformation())
                SettingsButton("保存手动配置") { savePairing(runCatching { TokenSyncEndpoint.parseManual(pairingHost, pairingSecret) }) }
            } else {
                SettingsButton("立即同步", onClick = ::syncPairedNow)
                SettingsButton("重新扫码", onClick = ::scanPairing)
                SettingsButton("解除配对", danger = true, onClick = ::clearPairing)
            }
            Text("LAN 同步仅适用于可信私人 Wi-Fi；不建议在公共 Wi-Fi 使用。", color = palette.color(palette.muted), fontSize = 12.sp)
        }
    }

    private fun renderState() {
        val alert = alertStore.load(); val refresh = refreshStore.load()
        lowQuota = alert.lowQuotaEnabled; resetAlert = alert.resetEnabled; notificationEnabled = notificationsEnabled()
        backgroundRefresh = refresh.enabled; refreshInterval = refresh.normalizedIntervalMinutes; themeMode = themeStore.load()
        pairing = tokenStore.load(); tokenStatus = if (pairing == null) "尚未配对 Windows" else "已配对"
        pairingHost = pairing?.let { "${it.host}:${it.port}" } ?: pairingHost
    }

    private fun selectRefreshInterval(minutes: Int) { refreshInterval = minutes; refreshStore.save(refreshStore.load().copy(intervalMinutes = minutes)); QuotaRefreshScheduler.schedule(this); AppLogStore.record(this, "后台刷新频率设为 $minutes 分钟") }
    private fun selectTheme(mode: ThemeMode) { if (themeStore.load() == mode) return; themeStore.save(mode); AppLogStore.record(this, "主题已切换"); recreate() }
    private fun requestNotificationPermission() {
        QuotaNotifications.ensureChannel(this)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), NOTIFICATION_REQUEST_CODE)
        else if (!notificationsEnabled()) openNotificationSettings()
        notificationEnabled = notificationsEnabled()
    }
    private fun sendTestNotification() {
        if (!notificationsEnabled()) { Toast.makeText(this, "请先开启系统通知", Toast.LENGTH_SHORT).show(); requestNotificationPermission(); return }
        if (QuotaNotifications.sendTest(this)) { AppLogStore.record(this, "已发送通知测试"); Toast.makeText(this, "测试通知已发送", Toast.LENGTH_SHORT).show() }
        else { AppLogStore.record(this, "通知测试未发送", "WARN"); Toast.makeText(this, "通知未发送，请检查系统通知权限", Toast.LENGTH_SHORT).show() }
    }
    private fun openNotificationSettings() { runCatching { startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, packageName)) }.onFailure { AppLogStore.record(this, "打开系统通知设置失败", "WARN") } }
    private fun openBatterySettings() { runCatching { startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)); AppLogStore.record(this, "打开电池优化设置") }.onFailure { Toast.makeText(this, "无法打开系统电池设置", Toast.LENGTH_SHORT).show() } }
    private fun notificationsEnabled(): Boolean {
        val permission = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        return permission && (getSystemService(NotificationManager::class.java)?.areNotificationsEnabled() ?: true)
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<String>, grantResults: IntArray) { super.onRequestPermissionsResult(requestCode, permissions, grantResults); if (requestCode == NOTIFICATION_REQUEST_CODE) notificationEnabled = notificationsEnabled() }

    private fun savePairing(result: Result<TokenSyncPairing>) {
        result.onSuccess { value ->
            if (tokenStore.save(value)) { pairingUri = ""; pairingSecret = ""; pairingHost = "${value.host}:${value.port}"; renderState(); Toast.makeText(this, "Token 同步配对已保存", Toast.LENGTH_SHORT).show(); testPairing(value) }
            else Toast.makeText(this, "无法安全保存配对信息", Toast.LENGTH_SHORT).show()
        }.onFailure { Toast.makeText(this, it.message ?: "配对信息无效", Toast.LENGTH_SHORT).show() }
    }
    private fun scanPairing() { IntentIntegrator(this).setDesiredBarcodeFormats("QR_CODE").setPrompt("扫描 Windows Token Usage 配对二维码").setBeepEnabled(false).setOrientationLocked(false).initiateScan() }
    @Suppress("DEPRECATION")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        val result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data)
        if (result != null) { if (!result.contents.isNullOrBlank()) savePairing(runCatching { TokenSyncEndpoint.parsePairingUri(result.contents) }) else Toast.makeText(this, "未读取二维码，可继续手动输入配对信息", Toast.LENGTH_SHORT).show(); return }
        super.onActivityResult(requestCode, resultCode, data)
    }
    private fun testPairing(value: TokenSyncPairing) {
        tokenStatus = "正在测试 Windows 连接…"
        pairingWorker.execute { val result = runCatching { TokenUsageSyncClient(this).sync(value) }; pairingMain.post { result.onSuccess { synced -> tokenStore.save(TokenSyncEndpoint.markSynced(synced.pairing, synced.snapshot)); renderState(); Toast.makeText(this, "Windows 配对成功", Toast.LENGTH_SHORT).show() }.onFailure { error -> renderState(); Toast.makeText(this, "已保存配对；${(error as? TokenUsageException)?.message ?: "Windows 当前不可用"}", Toast.LENGTH_LONG).show() } } }
    }
    private fun syncPairedNow() { tokenStore.load()?.let(::testPairing) ?: Toast.makeText(this, "尚未配对 Windows", Toast.LENGTH_SHORT).show() }
    private fun clearPairing() { if (tokenStore.clear()) { renderState(); Toast.makeText(this, "Windows 配对已解除", Toast.LENGTH_SHORT).show() } else Toast.makeText(this, "无法解除 Windows 配对", Toast.LENGTH_SHORT).show() }
    private fun formatPairingTime(raw: String) = runCatching { DateTimeFormatter.ofPattern("MM-dd HH:mm", java.util.Locale.getDefault()).withZone(ZoneId.systemDefault()).format(Instant.parse(raw)) }.getOrDefault("未知")

    companion object { private const val NOTIFICATION_REQUEST_CODE = 1002 }
}
