package com.codexquotatray.android

import android.os.SystemClock
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalLocale
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.codexquotatray.android.ui.formatResetCreditExpiry
import com.codexquotatray.android.ui.toQuotaUiModel
import com.codexquotatray.android.usage.RemoteMachine
import com.codexquotatray.android.usage.RemoteSyncClient
import com.codexquotatray.android.usage.RemoteSyncConnection
import com.codexquotatray.android.usage.RemoteSyncException
import com.codexquotatray.android.usage.RemoteSyncSnapshot
import com.codexquotatray.android.usage.RemoteSyncSettings
import com.codexquotatray.android.usage.RemoteSyncStore
import com.codexquotatray.android.usage.TokenUsageScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import java.text.NumberFormat
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
internal fun RemoteSyncOverview(
    store: RemoteSyncStore,
    resumeGeneration: Int,
    onConnection: () -> Unit,
    onWindows: () -> Unit,
    onEcs: () -> Unit,
) {
    val client = remember { RemoteSyncClient() }
    val locale = LocalLocale.current.platformLocale
    var settings by remember { mutableStateOf<RemoteSyncSettings?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(store, resumeGeneration) {
        settings = withContext(Dispatchers.IO) { store.load() }
        message = null
        val connection = settings?.connection ?: return@LaunchedEffect
        try {
            withContext(Dispatchers.IO) { store.readSnapshot(client, connection, RemoteMachine.WINDOWS) }
        } catch (error: RemoteSyncException) {
            message = error.userMessage
        } catch (_: IOException) {
            message = "无法连接到服务器。"
        }
        settings = withContext(Dispatchers.IO) { store.load() }
    }
    val connection = settings?.connection
    SettingsSection("服务器") {
        SettingsGroup {
            SettingsInfoRow(
                "地址",
                connection?.origin?.toString()?.removeSuffix("/") ?: "未配置",
                valueMaxLines = 4,
                wrapValue = true,
            )
            SettingsDivider()
            SettingsInfoRow(
                "最近连接成功",
                settings?.lastSuccessAtMillis?.let {
                    PairingTimeFormatter.format(it, System.currentTimeMillis(), ZoneId.systemDefault(), locale)
                } ?: "尚未成功连接",
            )
            SettingsDivider()
            SettingsNavigationRow("服务器连接", onClick = onConnection)
            message?.let { SettingsWarningCaption(it) }
        }
    }
    SettingsSection("查看设备") {
        SettingsGroup {
            SettingsNavigationRow(RemoteMachine.WINDOWS.label, enabled = connection != null, onClick = onWindows)
            SettingsDivider()
            SettingsNavigationRow(RemoteMachine.ECS.label, enabled = connection != null, onClick = onEcs)
        }
    }
}

@Composable
internal fun RemoteSyncConnectionPage(store: RemoteSyncStore, onFinished: () -> Unit) {
    val scope = rememberCoroutineScope()
    var initialized by remember { mutableStateOf(false) }
    var connection by remember { mutableStateOf<RemoteSyncConnection?>(null) }
    var saving by remember { mutableStateOf(false) }
    var configurationError by remember { mutableStateOf<String?>(null) }
    var address by remember { mutableStateOf("") }
    var readKey by remember { mutableStateOf("") }

    LaunchedEffect(store) {
        connection = withContext(Dispatchers.IO) { store.load().connection }
        address = connection?.origin?.toString().orEmpty()
        initialized = true
    }
    if (!initialized) return

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SettingsTextInput(
            value = address,
            onValueChange = { address = it.take(512) },
            label = "HTTPS 服务器地址",
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            enabled = !saving,
            placeholder = "https://sync.example.com",
        )
        SettingsTextInput(
            value = readKey,
            onValueChange = { readKey = it.take(64) },
            label = if (connection == null) "手机读取密钥" else "手机读取密钥（已保存）",
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            visualTransformation = PasswordVisualTransformation(),
            enabled = !saving,
            placeholder = if (connection == null) null else "使用原地址时可留空",
        )
        configurationError?.let { SettingsWarningCaption(it) }
        SettingsActionButton("保存连接", enabled = !saving, primary = true) {
            scope.launch {
                val next = try {
                    RemoteSyncConnection.parse(address, readKey, connection)
                } catch (error: IllegalArgumentException) {
                    configurationError = error.message
                    return@launch
                }
                saving = true
                if (withContext(Dispatchers.IO) { store.save(next) }) {
                    onFinished()
                } else {
                    configurationError = "无法保存远程同步配置。"
                }
                saving = false
            }
        }
        if (connection != null) {
            SettingsActionButton("移除连接", danger = true, enabled = !saving) {
                scope.launch {
                    saving = true
                    if (withContext(Dispatchers.IO) { store.clear() }) {
                        onFinished()
                    } else {
                        configurationError = "无法移除远程同步配置。"
                    }
                    saving = false
                }
            }
        }
    }
}

@Composable
internal fun RemoteSyncDevicePage(store: RemoteSyncStore, machine: RemoteMachine) {
    val client = remember { RemoteSyncClient() }
    val locale = LocalLocale.current.platformLocale
    var snapshot by remember { mutableStateOf<RemoteSyncSnapshot?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    var refreshing by remember { mutableStateOf(true) }
    var refresh by remember { mutableIntStateOf(0) }
    LaunchedEffect(refresh) {
        val presentationStartedAt = SystemClock.elapsedRealtime()
        message = null
        try {
            val connection = withContext(Dispatchers.IO) { store.load().connection }
            if (connection == null) {
                message = "请先配置服务器连接。"
            } else {
                snapshot = withContext(Dispatchers.IO) { store.readSnapshot(client, connection, machine) }
            }
        } catch (error: RemoteSyncException) {
            message = error.userMessage
        } catch (_: IOException) {
            message = "无法连接到服务器。"
        }
        delay(remainingRefreshPresentationMillis(presentationStartedAt, SystemClock.elapsedRealtime()))
        refreshing = false
    }

    SettingsGroup {
        SettingsActionButton(if (refreshing) "正在刷新…" else "刷新", enabled = !refreshing) {
            if (!refreshing) {
                refreshing = true
                refresh++
            }
        }
        if (!refreshing && message == null && snapshot == null) {
            SettingsInfoRow("状态", "暂无同步数据")
        }
        message?.let { SettingsWarningCaption(it) }
    }
    snapshot?.let { current ->
        SettingsSection("额度") {
            SettingsGroup {
                val quota = current.quota
                if (quota == null) {
                    SettingsInfoRow("状态", "暂无额度数据")
                } else {
                    SettingsInfoRow("采集时间", formatRemoteTime(Instant.ofEpochMilli(quota.updatedAtMillis)))
                    if (quota.windows.isEmpty()) SettingsInfoRow("状态", "暂无额度窗口")
                    quota.windows.forEach { window ->
                        val duration = window.windowDurationMins?.let { minutes ->
                            when {
                                minutes >= 1440 && minutes % 1440 == 0L -> "${minutes / 1440} 天"
                                minutes >= 60 && minutes % 60 == 0L -> "${minutes / 60} 小时"
                                else -> "$minutes 分钟"
                            }
                        }
                        val title = listOfNotNull(window.limitName, duration).joinToString(" · ").ifEmpty { "额度窗口" }
                        SettingsInfoRow(title, window.remainingPercent?.let { "剩余 $it%" } ?: "未知")
                        window.resetsAt?.let { seconds ->
                            runCatching { formatRemoteTime(Instant.ofEpochSecond(seconds), showAge = false) }
                                .getOrNull()?.let { SettingsInfoRow("重置时间", it) }
                        }
                    }
                    quota.resetCredits?.availableCount?.let { SettingsInfoRow("可用重置卡", "$it 张") }
                    quota.toQuotaUiModel().resetCredits?.availableCredits?.forEach { credit ->
                        SettingsInfoRow("到期时间", formatResetCreditExpiry(credit.expiresAt, ZoneId.systemDefault(), locale))
                    }
                }
            }
        }
        SettingsSection("Token 统计") {
            SettingsGroup {
                val usage = current.tokenUsage
                if (usage == null) {
                    SettingsInfoRow("状态", "暂无 Token 统计")
                } else {
                    current.tokenTime?.let { SettingsInfoRow("采集时间", formatRemoteTime(it)) }
                    SettingsInfoRow("统计范围", if (usage.scope == TokenUsageScope.LOCAL) "本机" else "账户")
                    SettingsInfoRow("统计时区", usage.sourceTimeZone)
                    val summary = usage.summary
                    val number = NumberFormat.getIntegerInstance()
                    listOf(
                        "今日" to summary.todayTokens,
                        "最近 7 天" to summary.last7DaysTokens,
                        "最近 30 天" to summary.last30DaysTokens,
                        "累计" to summary.lifetimeTokens,
                    ).forEach { (label, value) ->
                        SettingsInfoRow(label, value?.let { number.format(it) } ?: "未知")
                    }
                }
            }
        }
    }
}

private fun formatRemoteTime(time: Instant, showAge: Boolean = true): String {
    val formatted = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")
        .withZone(ZoneId.systemDefault()).format(time)
    return if (showAge && Duration.between(time, Instant.now()) >= Duration.ofMinutes(15)) {
        "$formatted（历史快照）"
    } else {
        formatted
    }
}
