package com.codexquotatray.android

import android.Manifest
import android.app.Activity
import android.app.NotificationManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import com.codexquotatray.android.alerts.QuotaNotifications
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaRefreshSettings
import com.codexquotatray.android.quota.QuotaRefreshSettingsStore
import com.codexquotatray.android.usage.TokenSyncEndpoint
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenUsageException
import com.codexquotatray.android.usage.TokenUsageSyncClient
import com.google.zxing.integration.android.IntentIntegrator
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.Executors
import kotlin.math.max

class SettingsActivity : Activity() {
    private val palette by lazy { AppTheme.palette(this) }
    private val alertSettingsStore by lazy { com.codexquotatray.android.alerts.QuotaAlertSettingsStore(this) }
    private val refreshSettingsStore by lazy { QuotaRefreshSettingsStore(this) }
    private val themeSettingsStore by lazy { ThemeSettingsStore(this) }
    private var updating = false

    private lateinit var lowQuotaSwitch: GlassToggleView
    private lateinit var resetSwitch: GlassToggleView
    private lateinit var notificationSwitch: GlassToggleView
    private lateinit var notificationStatus: TextView
    private lateinit var notificationTestButton: GlassActionButton
    private lateinit var backgroundRefreshSwitch: GlassToggleView
    private lateinit var refreshIntervalLabel: TextView
    private lateinit var refreshIntervalSpinner: Spinner
    private lateinit var systemThemeOption: TextView
    private lateinit var lightThemeOption: TextView
    private lateinit var darkThemeOption: TextView
    private lateinit var pairingUriInput: EditText
    private lateinit var tokenSyncHostInput: EditText
    private lateinit var tokenSyncSecretInput: EditText
    private lateinit var tokenSyncStatus: TextView
    private lateinit var tokenSyncDevice: TextView
    private lateinit var tokenSyncLastSync: TextView
    private lateinit var tokenSyncManualContainer: LinearLayout
    private lateinit var tokenSyncPairedActions: LinearLayout
    private lateinit var settingsRoot: FrameLayout
    private lateinit var settingsScroll: ScrollView
    private lateinit var settingsHeader: FrameLayout
    private val pairingWorker = Executors.newSingleThreadExecutor()
    private val pairingMain = android.os.Handler(android.os.Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContentView(buildContent())
        ViewCompat.requestApplyInsets(settingsRoot)
        render()
    }

    override fun onResume() {
        super.onResume()
        if (::notificationStatus.isInitialized) render()
    }

    override fun onDestroy() {
        pairingWorker.shutdownNow()
        super.onDestroy()
    }

    private fun buildContent(): View {
        settingsRoot = FrameLayout(this).apply {
            setBackgroundColor(palette.background)
            clipChildren = false
        }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(18), dp(20), dp(28))
        }
        settingsScroll = ScrollView(this).apply {
            isFillViewport = true
            clipToPadding = false
            addView(content)
        }
        settingsRoot.addView(settingsScroll, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT,
        ))
        settingsHeader = buildHeader()
        settingsRoot.addView(settingsHeader, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            dp(64),
            Gravity.TOP,
        ))

        content.addView(settingsSectionLabel(this, palette, "账户与同步"), marginParams(bottom = 8))
        val accountAndSync = SettingsGroupCard(this, palette)
        accountAndSync.addItem(accountRow(), dividerAfter = false)
        accountAndSync.addContent(tokenSyncSection())
        content.addView(accountAndSync, marginParams(bottom = 24))

        content.addView(settingsSectionLabel(this, palette, "额度提醒"), marginParams(bottom = 8))
        val alertGroup = SettingsGroupCard(this, palette)
        lowQuotaSwitch = glassToggle("低额度提醒") {
            if (!updating) {
                alertSettingsStore.save(alertSettingsStore.load().copy(lowQuotaEnabled = it))
                AppLogStore.record(this@SettingsActivity, "低额度提醒已${if (it) "开启" else "关闭"}")
            }
        }
        resetSwitch = glassToggle("额度重置提醒") {
            if (!updating) {
                alertSettingsStore.save(alertSettingsStore.load().copy(resetEnabled = it))
                AppLogStore.record(this@SettingsActivity, "额度重置提醒已${if (it) "开启" else "关闭"}")
            }
        }
        alertGroup.addItem(
            SettingsRow(
                this,
                palette,
                "低额度提醒",
                "额度跨过 50%、20% 或 10% 阈值时提醒。",
                lowQuotaSwitch,
            ) { lowQuotaSwitch.toggle() },
        )
        alertGroup.addItem(
            SettingsRow(
                this,
                palette,
                "额度重置提醒",
                "检测到额度窗口重置后提醒。",
                resetSwitch,
            ) { resetSwitch.toggle() },
        )
        content.addView(alertGroup, marginParams(bottom = 24))

        content.addView(settingsSectionLabel(this, palette, "通知与后台"), marginParams(bottom = 8))
        val systemGroup = SettingsGroupCard(this, palette)
        notificationSwitch = glassToggle("系统通知") {
            if (!updating) {
                AppLogStore.record(this@SettingsActivity, "系统通知设置已${if (it) "开启" else "关闭"}")
                if (it) requestNotificationPermission() else openNotificationSettings()
            }
        }
        systemGroup.addItem(
            SettingsRow(
                this,
                palette,
                "系统通知",
                "由 Android 管理权限；关闭后不会收到额度通知。",
                notificationSwitch,
            ) { notificationSwitch.toggle() },
        )
        notificationStatus = captionText("")
        systemGroup.addContent(notificationStatus, bottomMargin = 4)
        notificationTestButton = actionButton("发送测试通知") { sendTestNotification() }
        systemGroup.addContent(notificationTestButton, bottomMargin = 8)

        backgroundRefreshSwitch = glassToggle("后台自动刷新") {
            if (!updating) {
                refreshSettingsStore.save(refreshSettingsStore.load().copy(enabled = it))
                QuotaRefreshScheduler.schedule(this@SettingsActivity)
                AppLogStore.record(this@SettingsActivity, "后台自动刷新已${if (it) "开启" else "关闭"}")
                renderRefreshInterval()
            }
        }
        systemGroup.addItem(
            SettingsRow(
                this,
                palette,
                "后台自动刷新",
                "按设定频率在网络可用时读取额度。",
                backgroundRefreshSwitch,
            ) { backgroundRefreshSwitch.toggle() },
        )

        refreshIntervalLabel = textView("刷新频率", 16f, Typeface.BOLD).apply {
            setTextColor(palette.body)
        }
        refreshIntervalSpinner = Spinner(this).apply {
            backgroundTintList = android.content.res.ColorStateList.valueOf(palette.secondary)
            setPadding(dp(8), dp(4), dp(4), dp(4))
        }
        val intervalLabels = listOf("每 15 分钟", "每 30 分钟", "每 1 小时")
        refreshIntervalSpinner.adapter = ArrayAdapter(
            this,
            android.R.layout.simple_spinner_item,
            intervalLabels,
        ).also { adapter ->
            adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        }
        refreshIntervalSpinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                if (updating) return
                val interval = QuotaRefreshSettings.SUPPORTED_INTERVAL_MINUTES[position]
                refreshSettingsStore.save(
                    refreshSettingsStore.load().copy(intervalMinutes = interval),
                )
                QuotaRefreshScheduler.schedule(this@SettingsActivity)
                AppLogStore.record(this@SettingsActivity, "后台刷新频率设为 ${interval} 分钟")
            }

            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
        }
        val intervalRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(16), dp(10), dp(12), dp(10))
            addView(
                refreshIntervalLabel,
                LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
            )
            addView(
                refreshIntervalSpinner,
                LinearLayout.LayoutParams(dp(150), LinearLayout.LayoutParams.WRAP_CONTENT),
            )
        }
        intervalRow.background = rowBackground(palette)
        systemGroup.addContent(intervalRow, bottomMargin = 4)
        systemGroup.addContent(captionText("Android 可能因省电策略延迟后台任务。"), bottomMargin = 4)
        systemGroup.addContent(batteryOptimizationRow())
        content.addView(systemGroup, marginParams(bottom = 24))

        content.addView(settingsSectionLabel(this, palette, "外观主题"), marginParams(bottom = 8))
        val themeOptions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(dp(8), dp(8), dp(8), dp(8))
        }
        systemThemeOption = themeOption(ThemeMode.SYSTEM, "系统")
        lightThemeOption = themeOption(ThemeMode.LIGHT, "浅色")
        darkThemeOption = themeOption(ThemeMode.DARK, "暗色")
        themeOptions.addView(systemThemeOption, weightParams())
        themeOptions.addView(lightThemeOption, weightParams(left = 8))
        themeOptions.addView(darkThemeOption, weightParams(left = 8))
        val appearanceGroup = SettingsGroupCard(this, palette)
        appearanceGroup.addContent(
            captionText("选择跟随系统、浅色或暗色显示方式。"),
            bottomMargin = 4,
        )
        appearanceGroup.addContent(themeOptions)
        content.addView(appearanceGroup, marginParams(bottom = 24))

        content.addView(settingsSectionLabel(this, palette, "诊断与关于"), marginParams(bottom = 8))
        val diagnosticsGroup = SettingsGroupCard(this, palette)
        diagnosticsGroup.addItem(logRow())
        diagnosticsGroup.addItem(aboutRow(), dividerAfter = false)
        content.addView(diagnosticsGroup)

        ViewCompat.setOnApplyWindowInsetsListener(settingsRoot) { _, insets ->
            val safeTop = max(
                insets.getInsets(WindowInsetsCompat.Type.statusBars()).top,
                insets.getInsets(WindowInsetsCompat.Type.displayCutout()).top,
            )
            val safeBottom = max(
                insets.getInsets(WindowInsetsCompat.Type.navigationBars()).bottom,
                insets.getInsets(WindowInsetsCompat.Type.mandatorySystemGestures()).bottom,
            )
            val headerParams = settingsHeader.layoutParams
            headerParams.height = dp(64) + safeTop
            settingsHeader.layoutParams = headerParams
            settingsHeader.setPadding(0, safeTop, 0, 0)
            settingsScroll.setPadding(0, dp(64) + safeTop, 0, safeBottom + dp(28))
            insets
        }
        return settingsRoot
    }

    private fun render() {
        val alertSettings = alertSettingsStore.load()
        val refreshSettings = refreshSettingsStore.load()
        updating = true
        lowQuotaSwitch.setCheckedSilently(alertSettings.lowQuotaEnabled)
        resetSwitch.setCheckedSilently(alertSettings.resetEnabled)
        notificationSwitch.setCheckedSilently(notificationsEnabled())
        backgroundRefreshSwitch.setCheckedSilently(refreshSettings.enabled)
        refreshIntervalSpinner.setSelection(
            QuotaRefreshSettings.SUPPORTED_INTERVAL_MINUTES.indexOf(
                refreshSettings.normalizedIntervalMinutes,
            ).coerceAtLeast(0),
            false,
        )
        updating = false
        renderNotificationPermission()
        renderRefreshInterval()
        renderThemeOptions()
        renderTokenSyncPairing()
    }

    private fun renderNotificationPermission() {
        notificationStatus.text = if (notificationsEnabled()) {
            "当前状态：已开启"
        } else {
            "当前状态：未开启"
        }
    }

    private fun renderRefreshInterval() {
        val enabled = refreshSettingsStore.load().enabled
        refreshIntervalLabel.isEnabled = enabled
        refreshIntervalLabel.alpha = if (enabled) 1f else 0.45f
        refreshIntervalSpinner.isEnabled = enabled
        refreshIntervalSpinner.alpha = if (enabled) 1f else 0.45f
    }

    private fun renderThemeOptions() {
        val selected = themeSettingsStore.load()
        styleThemeOption(systemThemeOption, ThemeMode.SYSTEM, selected)
        styleThemeOption(lightThemeOption, ThemeMode.LIGHT, selected)
        styleThemeOption(darkThemeOption, ThemeMode.DARK, selected)
    }

    private fun styleThemeOption(option: TextView, mode: ThemeMode, selected: ThemeMode) {
        val isSelected = mode == selected
        option.text = if (isSelected) {
            "${themeTitle(mode)}\n当前使用"
        } else {
            themeTitle(mode)
        }
        option.setTextColor(if (isSelected) palette.onPrimary else palette.body)
        option.background = GradientDrawable().apply {
            setColor(if (isSelected) palette.primaryButton else palette.surface)
            setStroke(dp(if (isSelected) 2 else 1), if (isSelected) palette.primaryButton else palette.border)
            cornerRadius = dp(14).toFloat()
        }
        option.alpha = if (isSelected) 1f else 0.9f
    }

    private fun themeTitle(mode: ThemeMode): String = when (mode) {
        ThemeMode.SYSTEM -> "系统主题"
        ThemeMode.LIGHT -> "浅色主题"
        ThemeMode.DARK -> "暗色主题"
    }

    private fun selectTheme(mode: ThemeMode) {
        if (themeSettingsStore.load() == mode) return
        themeSettingsStore.save(mode)
        AppLogStore.record(this, "主题已切换为${themeTitle(mode)}")
        recreate()
    }

    private fun requestNotificationPermission() {
        QuotaNotifications.ensureChannel(this)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), NOTIFICATION_REQUEST_CODE)
            return
        }
        if (!notificationsEnabled()) openNotificationSettings()
        renderNotificationPermission()
    }

    private fun sendTestNotification() {
        if (!notificationsEnabled()) {
            Toast.makeText(this, "请先开启系统通知", Toast.LENGTH_SHORT).show()
            requestNotificationPermission()
            return
        }
        val sent = QuotaNotifications.sendTest(this)
        if (sent) {
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
                Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                    putExtra(Settings.EXTRA_APP_PACKAGE, packageName)
                },
            )
        }.onFailure {
            AppLogStore.record(this, "打开系统通知设置失败", "WARN")
        }
    }

    private fun openBatterySettings() {
        runCatching {
            startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            AppLogStore.record(this, "打开电池优化设置")
        }.onFailure {
            AppLogStore.record(this, "打开电池优化设置失败", "WARN")
            Toast.makeText(this, "无法打开系统电池设置", Toast.LENGTH_SHORT).show()
        }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == NOTIFICATION_REQUEST_CODE) {
            updating = true
            notificationSwitch.setCheckedSilently(notificationsEnabled())
            updating = false
            renderNotificationPermission()
        }
    }

    private fun notificationsEnabled(): Boolean {
        val permissionEnabled = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) ==
                PackageManager.PERMISSION_GRANTED
        } else {
            true
        }
        val manager = getSystemService(NotificationManager::class.java)
        return permissionEnabled && (manager?.areNotificationsEnabled() ?: true)
    }

    private fun buildHeader(): FrameLayout = FrameLayout(this).apply {
        setBackgroundColor(android.graphics.Color.TRANSPARENT)
        val title = textView("设置", 21f, Typeface.BOLD).apply {
            gravity = Gravity.CENTER
            setTextColor(palette.title)
        }
        addView(title, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT,
        ))
        addView(backButton(), FrameLayout.LayoutParams(dp(48), dp(48), Gravity.START or Gravity.CENTER_VERTICAL).apply {
            marginStart = dp(16)
        })
    }

    private fun glassToggle(label: String, onChanged: (Boolean) -> Unit): GlassToggleView =
        GlassToggleView(this, palette).apply {
            contentDescription = label
            setOnCheckedChangeListener { _, checked -> onChanged(checked) }
        }

    private fun captionText(value: String): TextView = textView(value, 12.5f, Typeface.NORMAL).apply {
        setTextColor(palette.muted)
        setPadding(dp(14), dp(2), dp(14), dp(2))
    }

    private fun rowBackground(palette: ThemePalette): GradientDrawable = GradientDrawable().apply {
        setColor(if (android.graphics.Color.luminance(palette.background) < 0.35f) {
            android.graphics.Color.argb(52, 255, 255, 255)
        } else {
            android.graphics.Color.argb(34, 0, 0, 0)
        })
        cornerRadius = dp(18).toFloat()
    }

    private fun inputBackground(): GradientDrawable = GradientDrawable().apply {
        setColor(if (android.graphics.Color.luminance(palette.background) < 0.35f) {
            android.graphics.Color.argb(70, 255, 255, 255)
        } else {
            palette.surface
        })
        setStroke(dp(1), android.graphics.Color.argb(48, android.graphics.Color.red(palette.title), android.graphics.Color.green(palette.title), android.graphics.Color.blue(palette.title)))
        cornerRadius = dp(16).toFloat()
    }

    private fun batteryOptimizationRow(): View = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(12), dp(12), dp(12), dp(12))
        addView(
            textView("电池优化", 16f, Typeface.BOLD).apply { setTextColor(palette.body) },
        )
        addView(
            textView(
                "如果后台刷新经常延迟，可在系统设置中将 CodexQuota 设为不受限制。",
                13f,
                Typeface.NORMAL,
            ).apply { setTextColor(palette.muted) },
            marginParams(top = 2, bottom = 8),
        )
        addView(actionButton("打开电池设置") { openBatterySettings() })
    }

    private fun themeOption(mode: ThemeMode, title: String): TextView = textView(title, 15f, Typeface.BOLD).apply {
        gravity = Gravity.CENTER
        minHeight = dp(76)
        setPadding(dp(8), dp(10), dp(8), dp(10))
        isClickable = true
        setOnClickListener { selectTheme(mode) }
    }

    private fun logRow(): View = SettingsRow(
        this,
        palette,
        "运行日志",
        "查看脱敏运行摘要",
        showChevron = true,
    ) { startActivity(Intent(this@SettingsActivity, LogActivity::class.java)) }

    private fun aboutRow(): View = SettingsRow(
        this,
        palette,
        "关于",
        "版本、许可与项目信息",
        showChevron = true,
    ) { startActivity(Intent(this@SettingsActivity, AboutActivity::class.java)) }

    private fun accountRow(): View = SettingsRow(
        this,
        palette,
        "账号管理",
        "查看当前 Codex 登录状态",
        showChevron = true,
    ) { startActivity(Intent(this@SettingsActivity, AccountActivity::class.java)) }

    private fun tokenSyncSection(): View = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(12), dp(10), dp(12), dp(12))
        addView(
            textView("Token 使用量同步", 16f, Typeface.BOLD).apply { setTextColor(palette.body) },
            marginParams(bottom = 8),
        )
        tokenSyncStatus = textView("尚未配对 Windows", 14f, Typeface.NORMAL).apply { setTextColor(palette.muted) }
        addView(tokenSyncStatus, marginParams(bottom = 4))
        tokenSyncDevice = textView("", 14f, Typeface.NORMAL).apply { setTextColor(palette.muted) }
        addView(tokenSyncDevice, marginParams(bottom = 4))
        tokenSyncLastSync = textView("", 13f, Typeface.NORMAL).apply { setTextColor(palette.muted) }
        addView(tokenSyncLastSync, marginParams(bottom = 10))
        addView(actionButton("扫描二维码") { scanPairing() }, marginParams(bottom = 12))

        tokenSyncManualContainer = LinearLayout(this@SettingsActivity).apply {
            orientation = LinearLayout.VERTICAL
        }
        pairingUriInput = EditText(this@SettingsActivity).apply {
            hint = "粘贴 codexquota://pair?..."
            setSingleLine(true)
            setTextColor(palette.body)
            setHintTextColor(palette.muted)
            background = inputBackground()
            setPadding(dp(14), dp(10), dp(14), dp(10))
        }
        tokenSyncManualContainer.addView(pairingUriInput, marginParams(bottom = 8))
        tokenSyncManualContainer.addView(actionButton("保存粘贴的配对信息") { savePairingUri() }, marginParams(bottom = 12))
        tokenSyncHostInput = EditText(this@SettingsActivity).apply {
            hint = "Windows 地址，例如 192.168.1.10:43821"
            setSingleLine(true)
            setTextColor(palette.body)
            setHintTextColor(palette.muted)
            background = inputBackground()
            setPadding(dp(14), dp(10), dp(14), dp(10))
        }
        tokenSyncManualContainer.addView(tokenSyncHostInput, marginParams(bottom = 8))
        tokenSyncSecretInput = EditText(this@SettingsActivity).apply {
            hint = "配对密钥"
            setSingleLine(true)
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD
            setTextColor(palette.body)
            setHintTextColor(palette.muted)
            background = inputBackground()
            setPadding(dp(14), dp(10), dp(14), dp(10))
        }
        tokenSyncManualContainer.addView(tokenSyncSecretInput, marginParams(bottom = 8))
        tokenSyncManualContainer.addView(actionButton("保存手动配置") { saveManualPairing() }, marginParams(bottom = 10))
        addView(tokenSyncManualContainer)

        tokenSyncPairedActions = LinearLayout(this@SettingsActivity).apply {
            orientation = LinearLayout.VERTICAL
        }
        tokenSyncPairedActions.addView(actionButton("立即同步") { syncPairedNow() }, marginParams(bottom = 8))
        tokenSyncPairedActions.addView(actionButton("重新扫码") { scanPairing() }, marginParams(bottom = 8))
        tokenSyncPairedActions.addView(actionButton("解除配对", danger = true) { clearPairing() }, marginParams(bottom = 10))
        addView(tokenSyncPairedActions)

        addView(textView("LAN 同步仅适用于可信私人 Wi-Fi；不建议在公共 Wi-Fi 使用。", 13f, Typeface.NORMAL).apply {
            setTextColor(palette.muted)
        }, marginParams(top = 10))
    }

    private fun savePairingUri() {
        savePairing(runCatching { TokenSyncEndpoint.parsePairingUri(pairingUriInput.text.toString()) })
    }

    private fun saveManualPairing() {
        savePairing(runCatching { TokenSyncEndpoint.parseManual(tokenSyncHostInput.text.toString(), tokenSyncSecretInput.text.toString()) })
    }

    private fun savePairing(result: Result<com.codexquotatray.android.usage.TokenSyncPairing>) {
        result.onSuccess { pairing ->
            if (TokenSyncStore(this).save(pairing)) {
                pairingUriInput.text.clear()
                tokenSyncSecretInput.text.clear()
                tokenSyncHostInput.setText("${pairing.host}:${pairing.port}")
                renderTokenSyncPairing()
                Toast.makeText(this, "Token 同步配对已保存", Toast.LENGTH_SHORT).show()
                testPairing(pairing)
            } else {
                Toast.makeText(this, "无法安全保存配对信息", Toast.LENGTH_SHORT).show()
            }
        }.onFailure { error -> Toast.makeText(this, error.message ?: "配对信息无效", Toast.LENGTH_SHORT).show() }
    }

    private fun scanPairing() {
        IntentIntegrator(this).apply {
            setDesiredBarcodeFormats("QR_CODE")
            setPrompt("扫描 Windows Token Usage 配对二维码")
            setBeepEnabled(false)
            setOrientationLocked(false)
        }.initiateScan()
    }

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

    private fun testPairing(pairing: com.codexquotatray.android.usage.TokenSyncPairing) {
        tokenSyncStatus.text = "正在测试 Windows 连接…"
        pairingWorker.execute {
            val result = runCatching { TokenUsageSyncClient(this).sync(pairing) }
            pairingMain.post {
                result.onSuccess { synced ->
                    val saved = TokenSyncEndpoint.markSynced(synced.pairing, synced.snapshot)
                    TokenSyncStore(this).save(saved)
                    renderTokenSyncPairing()
                    Toast.makeText(this, "Windows 配对成功", Toast.LENGTH_SHORT).show()
                }.onFailure { error ->
                    renderTokenSyncPairing()
                    val message = (error as? TokenUsageException)?.message ?: "Windows 当前不可用"
                    Toast.makeText(this, "已保存配对；$message", Toast.LENGTH_LONG).show()
                }
            }
        }
    }

    private fun syncPairedNow() {
        TokenSyncStore(this).load()?.let(::testPairing)
            ?: Toast.makeText(this, "尚未配对 Windows", Toast.LENGTH_SHORT).show()
    }

    private fun renderTokenSyncPairing() {
        if (!::tokenSyncStatus.isInitialized) return
        val stored = TokenSyncStore(this).load()
        if (stored == null) {
            tokenSyncStatus.text = "尚未配对 Windows"
            tokenSyncDevice.text = ""
            tokenSyncLastSync.text = ""
            tokenSyncHostInput.setText("")
            tokenSyncManualContainer.visibility = View.VISIBLE
            tokenSyncPairedActions.visibility = View.GONE
            return
        }

        tokenSyncStatus.text = "已配对"
        tokenSyncDevice.text = "电脑：${stored.displayName ?: "Windows PC"} · ${stored.host}:${stored.port}"
        tokenSyncLastSync.text = stored.lastSyncUtc?.let { "上次同步：${formatPairingTime(it)}" } ?: "上次同步：尚未成功"
        tokenSyncHostInput.setText("${stored.host}:${stored.port}")
        tokenSyncManualContainer.visibility = View.GONE
        tokenSyncPairedActions.visibility = View.VISIBLE
    }

    private fun clearPairing() {
        if (TokenSyncStore(this).clear()) {
            renderTokenSyncPairing()
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

    private fun actionButton(
        text: String,
        danger: Boolean = false,
        action: () -> Unit,
    ): GlassActionButton = GlassActionButton(this, palette, danger).apply {
        this.text = text
        setOnClickListener { action() }
    }

    private fun backButton(): GlassIconButton = GlassIconButton(
        this,
        palette,
        0,
        "返回",
    ).apply { setOnClickListener { finish() } }

    private fun textView(text: String, size: Float, style: Int): TextView = TextView(this).apply {
        this.text = text
        textSize = size
        setTypeface(typeface, style)
    }

    private fun marginParams(
        top: Int = 0,
        bottom: Int = 0,
        left: Int = 0,
        right: Int = 0,
    ): LinearLayout.LayoutParams = LinearLayout.LayoutParams(
        LinearLayout.LayoutParams.MATCH_PARENT,
        LinearLayout.LayoutParams.WRAP_CONTENT,
    ).apply {
        setMargins(dp(left), dp(top), dp(right), dp(bottom))
    }

    private fun weightParams(left: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
            setMargins(dp(left), 0, 0, 0)
        }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt().coerceAtLeast(value)

    companion object {
        private const val NOTIFICATION_REQUEST_CODE = 1002
    }
}
