package com.codexquotatray.android

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Intent
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.border
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.quota.CodexQuotaRepository
import com.codexquotatray.android.quota.QuotaReadException
import com.codexquotatray.android.quota.QuotaReadFailureKind
import com.codexquotatray.android.quota.QuotaRefreshEvents
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaRefreshSettingsStore
import com.codexquotatray.android.quota.QuotaSnapshotStore
import com.codexquotatray.android.refresh.AppAutomaticRefreshCoordinator
import com.codexquotatray.android.refresh.AutomaticRefreshChannel
import com.codexquotatray.android.refresh.AutomaticRefreshReason
import com.codexquotatray.android.ui.QuotaCardModel
import com.codexquotatray.android.ui.QuotaUiModel
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.ui.quotaErrorUiModel
import com.codexquotatray.android.ui.quotaLoadingUiModel
import com.codexquotatray.android.ui.toQuotaUiModel
import com.codexquotatray.android.ui.unauthenticatedQuotaUiModel
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.cacheIdentity
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

internal fun quotaProgressColor(remainingPercent: Int): Color = Color(quotaProgressArgb(remainingPercent))

internal fun quotaProgress(remainingPercent: Int): Float =
    remainingPercent.coerceIn(0, 100) / 100f

internal fun formatResetRemaining(remainingSeconds: Long): String {
    if (remainingSeconds <= 0L) return "已到期或正在刷新"
    val days = remainingSeconds / 86_400L
    val hours = (remainingSeconds % 86_400L) / 3_600L
    val minutes = (remainingSeconds % 3_600L) / 60L
    return when {
        days > 0L -> "$days 天 $hours 小时 $minutes 分钟"
        hours > 0L -> "$hours 小时 $minutes 分钟"
        minutes > 0L -> "$minutes 分钟"
        else -> "不足 1 分钟"
    }
}

/** Only a genuinely newer successful cache may replace a visible failure. */
internal fun hasNewerQuotaSnapshot(
    currentSuccessful: QuotaUiModel?,
    latestSuccessful: QuotaUiModel?,
): Boolean {
    if (latestSuccessful?.status != QuotaUiStatus.LOADED) return false
    val latestUpdatedAt = latestSuccessful.updatedAtMillis ?: return false
    val currentUpdatedAt = currentSuccessful?.updatedAtMillis
    return currentUpdatedAt == null || latestUpdatedAt > currentUpdatedAt
}

internal fun quotaSourceAvailable(oauthAvailable: Boolean, windowsPairingAvailable: Boolean): Boolean =
    oauthAvailable || windowsPairingAvailable

internal class QuotaPageController(private val host: MainActivity) {
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private val repository by lazy { CodexQuotaRepository(host) }
    private val refreshSettings by lazy { QuotaRefreshSettingsStore(host) }
    private val snapshotStore by lazy { QuotaSnapshotStore(host) }
    private val pairingStore by lazy { TokenSyncStore(host) }
    var model by mutableStateOf(unauthenticatedQuotaUiModel())
        private set
    var busy by mutableStateOf(false)
        private set
    private var lastSuccessful: QuotaUiModel? = null
    private var lastQuotaSourceAvailable: Boolean? = null
    private var lastWindowsDeviceIdentity: String? = null
    private var registered = false
    private var initialized = false
    private var visible = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            if (intent?.action == QuotaRefreshEvents.ACTION_COMPLETED && !busy) renderLatestSnapshot()
        }
    }

    val canRefresh get() = !busy && hasQuotaSource()

    fun initialize() {
        if (initialized) return
        initialized = true
        lastQuotaSourceAvailable = hasQuotaSource()
        lastWindowsDeviceIdentity = currentWindowsDeviceIdentity()
        QuotaRefreshScheduler.schedule(host)
        if (lastQuotaSourceAvailable == true) {
            lastSuccessful = loadLatestModel(lastWindowsDeviceIdentity)
            model = lastSuccessful ?: quotaLoadingUiModel(null)
        } else model = unauthenticatedQuotaUiModel()
    }

    fun onVisible() {
        visible = true
        val sourceAvailable = hasQuotaSource()
        val windowsDeviceIdentity = currentWindowsDeviceIdentity()
        val sourceChanged = lastQuotaSourceAvailable != sourceAvailable
        val pairingChanged = lastWindowsDeviceIdentity != windowsDeviceIdentity
        if (sourceChanged || pairingChanged) {
            lastQuotaSourceAvailable = sourceAvailable
            lastWindowsDeviceIdentity = windowsDeviceIdentity
            if (!sourceAvailable) {
                lastSuccessful = null
                snapshotStore.clear()
                com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(host)
                if (!busy) model = unauthenticatedQuotaUiModel()
                return
            }
            lastSuccessful = loadLatestModel(windowsDeviceIdentity)
            if (!busy || pairingChanged) model = lastSuccessful ?: quotaLoadingUiModel(null)
            QuotaRefreshScheduler.schedule(host)
        }
        if (sourceAvailable && !busy) renderLatestSnapshot()
    }

    fun onHidden() {
        visible = false
    }

    fun onStart() {
        if (registered) return
        val filter = IntentFilter(QuotaRefreshEvents.ACTION_COMPLETED)
        ContextCompat.registerReceiver(host, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        registered = true
    }

    fun onStop() {
        if (!registered) return
        host.unregisterReceiver(receiver)
        registered = false
    }

    fun onForeground(reason: AutomaticRefreshReason) {
        val sourceAvailable = hasQuotaSource()
        val windowsDeviceIdentity = currentWindowsDeviceIdentity()
        val sourceChanged = lastQuotaSourceAvailable != sourceAvailable
        val pairingChanged = lastWindowsDeviceIdentity != windowsDeviceIdentity
        if (sourceChanged || pairingChanged) {
            lastQuotaSourceAvailable = sourceAvailable
            lastWindowsDeviceIdentity = windowsDeviceIdentity
            if (!sourceAvailable) {
                lastSuccessful = null
                snapshotStore.clear()
                com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(host)
                if (!busy) model = unauthenticatedQuotaUiModel()
            } else {
                lastSuccessful = loadLatestModel(windowsDeviceIdentity)
                if (!busy || pairingChanged) model = lastSuccessful ?: quotaLoadingUiModel(null)
                QuotaRefreshScheduler.schedule(host)
            }
        }
        if (!sourceAvailable || busy) return
        renderLatestSnapshot()
        requestRefresh(reason)
    }

    fun onLoginResult(requestCode: Int, resultCode: Int) {
        if (requestCode == LOGIN_REQUEST_CODE && resultCode == Activity.RESULT_OK) QuotaRefreshScheduler.schedule(host)
    }

    fun destroy() { onStop(); worker.shutdownNow() }

    /** Manual refreshes deliberately bypass the two-minute foreground freshness window. */
    fun refresh() = requestRefresh(AutomaticRefreshReason.MANUAL)

    private fun requestRefresh(reason: AutomaticRefreshReason) {
        if (!canRefresh) return
        val enabled = refreshSettings.load().autoRefreshOnOpen
        if (!AppAutomaticRefreshCoordinator.tryStart(AutomaticRefreshChannel.QUOTA, reason, enabled)) return
        busy = true
        val previous = lastSuccessful
        val requestWindowsDeviceIdentity = currentWindowsDeviceIdentity()
        model = quotaLoadingUiModel(previous)
        worker.execute {
            val result = try {
                runCatching { repository.refresh() }
            } finally {
                AppAutomaticRefreshCoordinator.finish(AutomaticRefreshChannel.QUOTA)
            }
            main.post {
                busy = false
                val currentWindowsDeviceIdentity = currentWindowsDeviceIdentity()
                val directResultIsUsable = result.getOrNull()?.let {
                    it.source == QuotaSource.DIRECT && it.quotaState != "unavailable"
                } == true
                if (requestWindowsDeviceIdentity != currentWindowsDeviceIdentity &&
                    !directResultIsUsable
                ) {
                    lastWindowsDeviceIdentity = currentWindowsDeviceIdentity
                    lastQuotaSourceAvailable = hasQuotaSource()
                    lastSuccessful = loadLatestModel(currentWindowsDeviceIdentity)
                    model = lastSuccessful ?: if (lastQuotaSourceAvailable == true) {
                        quotaLoadingUiModel(null)
                    } else {
                        unauthenticatedQuotaUiModel()
                    }
                    return@post
                }
                model = result.fold(
                    onSuccess = { value ->
                        val candidate = value.toQuotaUiModel()
                        if (candidate.status == QuotaUiStatus.LOADED) {
                            lastSuccessful = candidate
                            candidate
                        } else {
                            AppLogStore.record(host, "额度详情暂不可用", "WARN")
                            quotaErrorUiModel(candidate.message ?: "额度详情暂不可用", previous)
                        }
                    },
                    onFailure = { error ->
                        AppLogStore.record(host, "额度读取失败：${error.message ?: "未知错误"}", "WARN")
                        if (error is QuotaReadException && error.kind == QuotaReadFailureKind.LOGIN_REQUIRED) {
                            lastQuotaSourceAvailable = hasQuotaSource()
                            if (lastQuotaSourceAvailable != true) {
                                lastSuccessful = null
                                snapshotStore.clear()
                                com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(host)
                                QuotaRefreshScheduler.cancel(host)
                            }
                        }
                        when (error) {
                            is QuotaReadException -> if (
                                error.kind == QuotaReadFailureKind.LOGIN_REQUIRED && lastQuotaSourceAvailable != true
                            ) {
                                unauthenticatedQuotaUiModel()
                            } else {
                                quotaErrorUiModel(error.message, previous)
                            }
                            else -> quotaErrorUiModel("额度读取失败", previous)
                        }
                    },
                )
            }
        }
    }

    private fun hasQuotaSource(): Boolean = quotaSourceAvailable(
        oauthAvailable = OAuthStore(host).load() != null,
        windowsPairingAvailable = pairingStore.load() != null,
    )

    fun openLogin() = host.startActivityForResult(Intent(host, LoginActivity::class.java), LOGIN_REQUEST_CODE)

    private fun currentWindowsDeviceIdentity(): String? = pairingStore.load()?.cacheIdentity()

    private fun loadLatestModel(windowsDeviceIdentity: String? = currentWindowsDeviceIdentity()) = snapshotStore.load(windowsDeviceIdentity)
        ?.takeIf { it.quotaState != "unavailable" }
        ?.toQuotaUiModel()
        ?.takeIf { it.status == QuotaUiStatus.LOADED }
    private fun renderLatestSnapshot() {
        val latest = loadLatestModel(currentWindowsDeviceIdentity()) ?: return
        if (!hasNewerQuotaSnapshot(lastSuccessful, latest)) return
        lastSuccessful = latest
        model = latest
    }

    companion object { private const val LOGIN_REQUEST_CODE = 1003 }
}

@Composable
internal fun QuotaPage(
    controller: QuotaPageController,
    modifier: Modifier = Modifier,
) {
    val palette = LocalQuotaPalette.current
    val model = controller.model
    Column(
        modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text(
            "额度卡片",
            fontSize = 21.sp,
            fontWeight = FontWeight.Bold,
            color = palette.color(palette.title),
        )
        QuotaStatusLine(model)
        if (model.status != QuotaUiStatus.UNAUTHENTICATED) {
            if (model.status == QuotaUiStatus.LOADED && model.windows.isEmpty()) Text("当前没有可用额度窗口")
            model.windows.forEach { QuotaWindowCard(it) }
        } else {
            Button(onClick = rememberSystemHapticClick(controller::openLogin), enabled = !controller.busy, modifier = Modifier.fillMaxWidth()) { Text("登录 Codex") }
        }
        Spacer(Modifier.height(96.dp))
    }
}

@Composable
private fun QuotaStatusLine(model: QuotaUiModel) {
    RefreshStatusLine(quotaStatusLine(model))
}

private fun quotaStatusLine(model: QuotaUiModel): String {
    val updatedAt = model.updatedAtMillis?.let(::formatClockTime)
    return when (model.status) {
        QuotaUiStatus.LOADING -> RefreshStatusFormatter.refreshing(model.updatedAtMillis != null)
        QuotaUiStatus.UNAUTHENTICATED -> RefreshStatusFormatter.quotaNoSource()
        QuotaUiStatus.LOADED -> RefreshStatusFormatter.loaded(
            source = if (model.source == QuotaSource.WINDOWS) "Windows" else "OpenAI",
            updatedAt = updatedAt,
        )
        QuotaUiStatus.ERROR -> RefreshStatusFormatter.failure(
            reason = shortQuotaRefreshFailure(model.message),
            updatedAt = updatedAt,
        )
    }
}

@Composable
private fun QuotaWindowCard(window: QuotaCardModel) {
    val palette = LocalQuotaPalette.current
    val remainingPercent = window.remainingPercent
    val progressTarget = remainingPercent?.let(::quotaProgress) ?: 0f
    val colorTarget = remainingPercent?.let(::quotaProgressColor) ?: palette.color(palette.accent)
    val animatedProgress by animateFloatAsState(
        targetValue = progressTarget,
        animationSpec = tween(durationMillis = QUOTA_PROGRESS_ANIMATION_MILLIS),
        label = "quota-progress",
    )
    val animatedColor by animateColorAsState(
        targetValue = colorTarget,
        animationSpec = tween(durationMillis = QUOTA_PROGRESS_ANIMATION_MILLIS),
        label = "quota-progress-color",
    )
    val cardShape = RoundedCornerShape(18.dp)
    val dark = palette.color(palette.background).luminance() < 0.1f
    val cardBrush = if (dark) {
        Brush.linearGradient(
            listOf(
                Color(0xFF2A3037).copy(alpha = 0.76f),
                Color(0xFF17191D).copy(alpha = 0.94f),
                Color(0xFF101216).copy(alpha = 0.97f),
            ),
        )
    } else {
        Brush.linearGradient(
            listOf(
                Color.White.copy(alpha = 0.96f),
                palette.color(palette.surface).copy(alpha = 0.9f),
                palette.color(palette.surface).copy(alpha = 0.98f),
            ),
        )
    }
    val borderBrush = Brush.linearGradient(
        listOf(
            if (dark) Color.White.copy(alpha = 0.22f) else palette.color(palette.border),
            if (dark) Color.White.copy(alpha = 0.07f) else palette.color(palette.border).copy(alpha = 0.72f),
        ),
    )
    Box(
        Modifier
            .fillMaxWidth()
            .background(cardBrush, cardShape)
            .border(1.dp, borderBrush, cardShape)
            .padding(16.dp),
    ) {
        Row(
            Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            QuotaProgressRing(
                progress = animatedProgress,
                progressColor = animatedColor,
                trackColor = palette.color(palette.progressTrack),
                remainingPercent = remainingPercent,
            )
            Spacer(Modifier.size(16.dp))
            Column(
                Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(7.dp),
            ) {
                Text(
                    window.title,
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold,
                    color = palette.color(palette.title),
                )
                Text(
                    formatResetAt(window.resetsAt),
                    color = palette.color(palette.secondary),
                    fontSize = 14.sp,
                )
                Text(
                    formatRemaining(window.resetsAt),
                    color = palette.color(palette.secondary),
                    fontSize = 14.sp,
                )
            }
        }
    }
}

@Composable
private fun QuotaProgressRing(
    progress: Float,
    progressColor: Color,
    trackColor: Color,
    remainingPercent: Int?,
) {
    Box(Modifier.size(128.dp), contentAlignment = Alignment.Center) {
        Canvas(Modifier.fillMaxSize().padding(4.dp)) {
            val strokeWidth = 10.dp.toPx()
            val glowWidth = 22.dp.toPx()
            val inset = glowWidth / 2f
            val arcSize = Size(size.width - glowWidth, size.height - glowWidth)
            val arcStyle = Stroke(width = strokeWidth, cap = StrokeCap.Round)
            drawArc(
                color = trackColor.copy(alpha = 0.72f),
                startAngle = -90f,
                sweepAngle = 360f,
                useCenter = false,
                topLeft = Offset(inset, inset),
                size = arcSize,
                style = arcStyle,
            )
            if (progress > 0f) {
                val sweep = 360f * progress.coerceIn(0f, 1f)
                drawArc(
                    color = progressColor.copy(alpha = 0.16f),
                    startAngle = -90f,
                    sweepAngle = sweep,
                    useCenter = false,
                    topLeft = Offset(inset, inset),
                    size = arcSize,
                    style = Stroke(width = glowWidth, cap = StrokeCap.Round),
                )
                drawArc(
                    color = progressColor.copy(alpha = 0.28f),
                    startAngle = -90f,
                    sweepAngle = sweep,
                    useCenter = false,
                    topLeft = Offset(inset, inset),
                    size = arcSize,
                    style = Stroke(width = strokeWidth + 3.dp.toPx(), cap = StrokeCap.Round),
                )
                drawArc(
                    color = progressColor,
                    startAngle = -90f,
                    sweepAngle = sweep,
                    useCenter = false,
                    topLeft = Offset(inset, inset),
                    size = arcSize,
                    style = arcStyle,
                )
                drawArc(
                    color = Color.White.copy(alpha = 0.2f),
                    startAngle = -90f,
                    sweepAngle = sweep,
                    useCenter = false,
                    topLeft = Offset(inset, inset),
                    size = arcSize,
                    style = Stroke(width = 1.dp.toPx(), cap = StrokeCap.Round),
                )
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                remainingPercent?.let { "$it%" } ?: "—",
                fontSize = 28.sp,
                fontWeight = FontWeight.Bold,
                color = progressColor,
            )
            Text("余额", fontSize = 12.sp, color = LocalQuotaPalette.current.color(LocalQuotaPalette.current.secondary))
        }
    }
}

private const val QUOTA_PROGRESS_ANIMATION_MILLIS = 350

private fun formatResetAt(epochSeconds: Long?): String {
    if (epochSeconds == null) return "重置时间未知"
    val absolute = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault()).format(Date(epochSeconds * 1_000L))
    return "重置于 $absolute"
}

private fun formatRemaining(epochSeconds: Long?): String {
    if (epochSeconds == null) return "剩余时间未知"
    return "剩余 ${formatResetRemaining(epochSeconds - System.currentTimeMillis() / 1_000L)}"
}

private fun formatClockTime(epochMillis: Long) = SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(epochMillis))
