package com.codexquotatray.android

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.Handler
import android.os.Looper
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.quota.CodexQuotaRepository
import com.codexquotatray.android.quota.QuotaReadException
import com.codexquotatray.android.quota.QuotaReadFailureKind
import com.codexquotatray.android.quota.QuotaRefreshEvents
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaSnapshotStore
import com.codexquotatray.android.ui.QuotaCardModel
import com.codexquotatray.android.ui.QuotaUiModel
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.ui.quotaErrorUiModel
import com.codexquotatray.android.ui.quotaLoadingUiModel
import com.codexquotatray.android.ui.toQuotaUiModel
import com.codexquotatray.android.ui.unauthenticatedQuotaUiModel
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

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

internal class QuotaPageController(private val host: MainActivity) {
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private val repository by lazy { CodexQuotaRepository(host) }
    private val snapshotStore by lazy { QuotaSnapshotStore(host) }
    var model by mutableStateOf(unauthenticatedQuotaUiModel())
        private set
    var busy by mutableStateOf(false)
        private set
    private var lastSuccessful: QuotaUiModel? = null
    private var lastAuthenticated: Boolean? = null
    private var registered = false
    private var initialized = false

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
            model = quotaLoadingUiModel(lastSuccessful)
            refresh()
        } else model = unauthenticatedQuotaUiModel()
    }

    fun onStart() {
        if (registered) return
        val filter = IntentFilter(QuotaRefreshEvents.ACTION_COMPLETED)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) host.registerReceiver(receiver, filter, android.content.Context.RECEIVER_NOT_EXPORTED)
        else @Suppress("DEPRECATION") host.registerReceiver(receiver, filter)
        registered = true
    }

    fun onStop() {
        if (!registered) return
        host.unregisterReceiver(receiver)
        registered = false
    }

    fun onResume() {
        val authenticated = OAuthStore(host).load() != null
        if (lastAuthenticated == authenticated) {
            if (authenticated && !busy) renderLatestSnapshot()
            return
        }
        lastAuthenticated = authenticated
        if (!authenticated) {
            lastSuccessful = null
            snapshotStore.clear()
            if (!busy) model = unauthenticatedQuotaUiModel()
        } else if (!busy) {
            lastSuccessful = null
            QuotaRefreshScheduler.schedule(host)
            refresh()
        }
    }

    fun onLoginResult(requestCode: Int, resultCode: Int) {
        if (requestCode == LOGIN_REQUEST_CODE && resultCode == Activity.RESULT_OK) QuotaRefreshScheduler.schedule(host)
    }

    fun destroy() { onStop(); worker.shutdownNow() }

    fun refresh() {
        if (!canRefresh) return
        busy = true
        val previous = lastSuccessful
        model = quotaLoadingUiModel(previous)
        worker.execute {
            val result = runCatching { repository.refresh() }
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
        if ((latest.updatedAtMillis ?: return) < (lastSuccessful?.updatedAtMillis ?: Long.MIN_VALUE)) return
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
        if (model.status != QuotaUiStatus.UNAUTHENTICATED) Text("${model.accountLabel} / 当前账户", color = palette.color(palette.secondary), fontSize = 16.sp)
        Text(
            model.message ?: when (model.status) {
                QuotaUiStatus.LOADING -> "正在读取额度…"
                QuotaUiStatus.UNAUTHENTICATED -> "尚未登录 Codex"
                QuotaUiStatus.LOADED -> "额度读取成功"
                QuotaUiStatus.ERROR -> "额度读取失败"
            },
            color = palette.color(if (model.status == QuotaUiStatus.ERROR) palette.error else palette.body),
            fontSize = 16.sp,
        )
        if (model.status != QuotaUiStatus.UNAUTHENTICATED) {
            if (model.status == QuotaUiStatus.LOADED && model.windows.isEmpty()) Text("当前没有可用额度窗口")
            model.windows.forEach { QuotaWindowCard(it) }
            Text(model.updatedAtMillis?.let { "更新于 ${formatClockTime(it)}" } ?: "尚未更新", color = palette.color(palette.muted), fontSize = 13.sp)
        } else {
            Button(onClick = controller::openLogin, enabled = !controller.busy, modifier = Modifier.fillMaxWidth()) { Text("登录 Codex") }
        }
        Spacer(Modifier.height(96.dp))
    }
}

@Composable
private fun QuotaWindowCard(window: QuotaCardModel) {
    val palette = LocalQuotaPalette.current
    Card(
        Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface)),
        elevation = CardDefaults.cardElevation(defaultElevation = 2.dp),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(window.title, fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Text(window.remainingPercent?.let { "剩余 $it%" } ?: "剩余未知", fontSize = 26.sp, fontWeight = FontWeight.Bold, color = palette.color(palette.accent))
            window.remainingPercent?.let { LinearProgressIndicator(progress = { it.coerceIn(0, 100) / 100f }, modifier = Modifier.fillMaxWidth().height(8.dp), color = palette.color(palette.accent), trackColor = palette.color(palette.progressTrack)) }
            Text(formatResetAt(window.resetsAt), color = palette.color(palette.secondary), fontSize = 14.sp)
        }
    }
}

private fun formatResetAt(epochSeconds: Long?): String {
    if (epochSeconds == null) return "重置时间未知"
    val absolute = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault()).format(Date(epochSeconds * 1_000L))
    return "重置于 $absolute\n${formatResetRemaining(epochSeconds - System.currentTimeMillis() / 1_000L)}"
}

private fun formatClockTime(epochMillis: Long) = SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(epochMillis))
