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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.shadow.Shadow
import androidx.compose.ui.draw.dropShadow
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.DpOffset
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
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

private val QuotaProgressGreen = Color(0xFF35E66B)
private val QuotaProgressYellow = Color(0xFFFFD84D)
private val QuotaProgressRed = Color(0xFFFF4D5D)

internal fun quotaProgressColor(remainingPercent: Int): Color {
    val remaining = remainingPercent.coerceIn(0, 100) / 100f
    return if (remaining >= 0.5f) {
        lerp(QuotaProgressYellow, QuotaProgressGreen, (remaining - 0.5f) * 2f)
    } else {
        lerp(QuotaProgressRed, QuotaProgressYellow, remaining * 2f)
    }
}

internal fun quotaProgress(remainingPercent: Int): Float =
    remainingPercent.coerceIn(0, 100) / 100f

internal fun formatResetRemaining(remainingSeconds: Long): String {
    if (remainingSeconds <= 0L) return "已到期或正在刷新"
    val days = remainingSeconds / 86_400L
    val hours = (remainingSeconds % 86_400L) / 3_600L
    val minutes = (remainingSeconds % 3_600L) / 60L
    return when {
        days > 0L -> "$days 天 $hours 小时 $minutes 分钟后重置"
        hours > 0L -> "$hours 小时 $minutes 分钟后重置"
        minutes > 0L -> "$minutes 分钟后重置"
        else -> "不足 1 分钟后重置"
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

internal class QuotaPageController(private val host: MainActivity) {
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private val repository by lazy { CodexQuotaRepository(host) }
    private val refreshSettings by lazy { QuotaRefreshSettingsStore(host) }
    private val snapshotStore by lazy { QuotaSnapshotStore(host) }
    var model by mutableStateOf(unauthenticatedQuotaUiModel())
        private set
    var busy by mutableStateOf(false)
        private set
    private var lastSuccessful: QuotaUiModel? = null
    private var lastAuthenticated: Boolean? = null
    private var registered = false
    private var initialized = false
    private var visible = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            if (intent?.action == QuotaRefreshEvents.ACTION_COMPLETED && !busy) renderLatestSnapshot()
        }
    }

    val canRefresh get() = !busy && OAuthStore(host).load() != null

    fun initialize() {
        if (initialized) return
        initialized = true
        lastAuthenticated = OAuthStore(host).load() != null
        QuotaRefreshScheduler.schedule(host)
        if (lastAuthenticated == true) {
            lastSuccessful = loadLatestModel()
            model = lastSuccessful ?: quotaLoadingUiModel(null)
        } else model = unauthenticatedQuotaUiModel()
    }

    fun onVisible() {
        visible = true
        val authenticated = OAuthStore(host).load() != null
        if (lastAuthenticated != authenticated) {
            lastAuthenticated = authenticated
            if (!authenticated) {
                lastSuccessful = null
                snapshotStore.clear()
                if (!busy) model = unauthenticatedQuotaUiModel()
                return
            }
            lastSuccessful = null
            QuotaRefreshScheduler.schedule(host)
        }
        if (authenticated && !busy) renderLatestSnapshot()
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
        val authenticated = OAuthStore(host).load() != null
        if (lastAuthenticated != authenticated) {
            lastAuthenticated = authenticated
            if (!authenticated) {
                lastSuccessful = null
                snapshotStore.clear()
                if (!busy) model = unauthenticatedQuotaUiModel()
            } else {
                lastSuccessful = null
                QuotaRefreshScheduler.schedule(host)
            }
        }
        if (!authenticated || busy) return
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
        model = quotaLoadingUiModel(previous)
        worker.execute {
            val result = try {
                runCatching { repository.refresh() }
            } finally {
                AppAutomaticRefreshCoordinator.finish(AutomaticRefreshChannel.QUOTA)
            }
            main.post {
                busy = false
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
                            lastAuthenticated = false; lastSuccessful = null; snapshotStore.clear(); QuotaRefreshScheduler.cancel(host)
                        }
                        when (error) {
                            is QuotaReadException -> if (error.kind == QuotaReadFailureKind.LOGIN_REQUIRED) unauthenticatedQuotaUiModel() else quotaErrorUiModel(error.message, previous)
                            else -> quotaErrorUiModel("额度读取失败", previous)
                        }
                    },
                )
            }
        }
    }

    fun openLogin() = host.startActivityForResult(Intent(host, LoginActivity::class.java), LOGIN_REQUEST_CODE)

    private fun loadLatestModel() = snapshotStore.load()?.takeIf { it.quotaState != "unavailable" }?.toQuotaUiModel()?.takeIf { it.status == QuotaUiStatus.LOADED }
    private fun renderLatestSnapshot() {
        val latest = loadLatestModel() ?: return
        if (!hasNewerQuotaSnapshot(lastSuccessful, latest)) return
        lastSuccessful = latest
        model = latest
    }

    companion object { private const val LOGIN_REQUEST_CODE = 1003 }
}

@Composable
internal fun QuotaPage(controller: QuotaPageController, modifier: Modifier = Modifier) {
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
    val palette = LocalQuotaPalette.current
    val isNetworkError = model.status == QuotaUiStatus.ERROR && model.message?.contains("无法连接") == true
    val lastSync = model.updatedAtMillis?.let { "上次同步于 ${formatClockTime(it)}" }
    if (isNetworkError && lastSync != null) {
        Row {
            Text(lastSync, color = palette.color(palette.muted), fontSize = 14.sp)
            Text(" · ", color = palette.color(palette.muted), fontSize = 14.sp)
            Text("网络连接异常", color = palette.color(palette.error), fontSize = 14.sp)
        }
        return
    }
    Text(
        text = if (isNetworkError) "网络连接异常" else quotaStatusLine(model),
        color = palette.color(if (model.status == QuotaUiStatus.ERROR) palette.error else palette.muted),
        fontSize = 14.sp,
    )
}

private fun quotaStatusLine(model: QuotaUiModel): String {
    val status = model.message ?: when (model.status) {
        QuotaUiStatus.LOADING -> "正在读取额度…"
        QuotaUiStatus.UNAUTHENTICATED -> "尚未登录 Codex"
        QuotaUiStatus.LOADED -> "额度读取成功"
        QuotaUiStatus.ERROR -> "额度读取失败"
    }
    if (model.status != QuotaUiStatus.LOADED) return status
    val updatedAt = model.updatedAtMillis?.let { "更新于 ${formatClockTime(it)}" } ?: "尚未更新"
    val source = if (model.source == QuotaSource.WINDOWS) " · Windows" else ""
    return "$status · $updatedAt$source"
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
    Card(
        Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface)),
        elevation = CardDefaults.cardElevation(defaultElevation = 2.dp),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(window.title, fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Text(
                remainingPercent?.let { "剩余 $it%" } ?: "剩余未知",
                fontSize = 26.sp,
                fontWeight = FontWeight.Bold,
                color = animatedColor,
            )
            remainingPercent?.let {
                QuotaGlowProgressBar(
                    progress = animatedProgress,
                    progressColor = animatedColor,
                    trackColor = palette.color(palette.progressTrack),
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            Text(formatResetAt(window.resetsAt), color = palette.color(palette.secondary), fontSize = 14.sp)
        }
    }
}

@Composable
private fun QuotaGlowProgressBar(
    progress: Float,
    progressColor: Color,
    trackColor: Color,
    modifier: Modifier = Modifier,
) {
    val capsule = RoundedCornerShape(percent = 50)
    Box(modifier.height(21.dp), contentAlignment = Alignment.CenterStart) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(7.dp)
                .background(trackColor, capsule),
        )
        if (progress > 0f) {
            Box(
                Modifier
                    .fillMaxWidth(progress.coerceIn(0f, 1f))
                    .height(7.dp)
                    .dropShadow(
                        shape = capsule,
                        shadow = Shadow(
                            radius = 8.dp,
                            spread = 1.dp,
                            color = progressColor.copy(alpha = 0.28f),
                            offset = DpOffset.Zero,
                        ),
                    )
                    .background(progressColor, capsule),
            )
        }
    }
}

private const val QUOTA_PROGRESS_ANIMATION_MILLIS = 350

private fun formatResetAt(epochSeconds: Long?): String {
    if (epochSeconds == null) return "重置时间未知"
    val absolute = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault()).format(Date(epochSeconds * 1_000L))
    return "重置于 $absolute\n${formatResetRemaining(epochSeconds - System.currentTimeMillis() / 1_000L)}"
}

private fun formatClockTime(epochMillis: Long) = SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(epochMillis))
