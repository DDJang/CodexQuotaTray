package com.codexquotatray.android.update

import android.content.Context
import android.os.Handler
import android.os.Looper
import com.codexquotatray.android.BuildConfig
import java.util.concurrent.Executor
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

private const val AUTOMATIC_CHECK_INTERVAL_MILLIS = 24L * 60L * 60L * 1000L

class UpdateCheckCoordinator(
    private val settings: UpdateSettingsRepository,
    private val providerFor: (UpdateSource) -> UpdateProvider,
    private val currentVersionName: String,
    private val executor: Executor = Executors.newSingleThreadExecutor(),
    private val callbackExecutor: Executor = Executor { it.run() },
    private val nowMillis: () -> Long = System::currentTimeMillis,
) : AutoCloseable {
    private val lock = Any()
    private var inFlight: MutableList<(UpdateCheckResult) -> Unit>? = null
    private val reminderListeners = linkedSetOf<(UpdateRelease) -> Unit>()

    constructor(context: Context) : this(
        settings = UpdateSettingsStore(context),
        providerFor = { source ->
            when (source) {
                UpdateSource.GITHUB -> GithubUpdateProvider()
                UpdateSource.GITEE -> UnavailableUpdateProvider
            }
        },
        currentVersionName = BuildConfig.VERSION_NAME,
        executor = Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "codex-update-check").apply { isDaemon = true }
        },
        callbackExecutor = Executor { runnable -> Handler(Looper.getMainLooper()).post(runnable) },
    )

    fun addReminderListener(listener: (UpdateRelease) -> Unit): AutoCloseable {
        synchronized(lock) { reminderListeners += listener }
        return AutoCloseable { synchronized(lock) { reminderListeners -= listener } }
    }

    fun check(
        reason: UpdateCheckReason,
        callback: (UpdateCheckResult) -> Unit,
    ) {
        val currentSettings = settings.load()
        if (reason == UpdateCheckReason.AUTOMATIC && !currentSettings.automaticChecksEnabled) {
            dispatch(callback, UpdateCheckResult.Skipped(SkipReason.AUTO_DISABLED))
            return
        }
        val now = nowMillis()
        synchronized(lock) {
            if (inFlight != null) {
                // A manual caller joins the current request rather than starting a second one.
                inFlight!!.add(callback)
                return
            }
            if (reason == UpdateCheckReason.AUTOMATIC && !shouldRunAutomaticCheck(currentSettings.lastCheckAtMillis, now)) {
                dispatch(callback, UpdateCheckResult.Skipped(SkipReason.WITHIN_INTERVAL))
                return
            }
            settings.save(currentSettings.copy(lastCheckAtMillis = now))
            inFlight = mutableListOf(callback)
        }
        executor.execute {
            val result = runCatching { checkNow(currentSettings.source) }
                .getOrElse { error -> UpdateCheckResult.Failed(error.message ?: "检查更新失败", error) }
            val callbacks = synchronized(lock) {
                val current = inFlight.orEmpty().toList()
                inFlight = null
                current
            }
            callbacks.forEach { dispatch(it, result) }
            if (reason == UpdateCheckReason.AUTOMATIC) maybeNotify(result)
        }
    }

    fun requestAutomaticCheck(callback: ((UpdateCheckResult) -> Unit)? = null) {
        check(UpdateCheckReason.AUTOMATIC) { result -> callback?.invoke(result) }
    }

    private fun checkNow(source: UpdateSource): UpdateCheckResult {
        if (!source.available) return UpdateCheckResult.Skipped(SkipReason.SOURCE_UNAVAILABLE)
        val current = SemVer.parse(currentVersionName)
            ?: return UpdateCheckResult.Failed("当前应用版本无效")
        val release = providerFor(source).fetchLatest()
        if (release.androidAsset == null) return UpdateCheckResult.NoAndroidAsset(release)
        return if (release.version > current) {
            UpdateCheckResult.Available(release, current)
        } else {
            UpdateCheckResult.UpToDate(current, release.version)
        }
    }

    private fun maybeNotify(result: UpdateCheckResult) {
        val available = result as? UpdateCheckResult.Available ?: return
        val current = settings.load()
        if (!current.updateRemindersEnabled) return
        val version = available.release.version.toString()
        if (current.lastNotifiedVersion == version) return
        settings.save(current.copy(lastNotifiedVersion = version))
        val listeners = synchronized(lock) { reminderListeners.toList() }
        listeners.forEach { listener -> callbackExecutor.execute { listener(available.release) } }
    }

    private fun dispatch(callback: (UpdateCheckResult) -> Unit, result: UpdateCheckResult) {
        callbackExecutor.execute { callback(result) }
    }

    override fun close() {
        (executor as? ExecutorService)?.shutdownNow()
    }

    companion object {
        internal const val AUTOMATIC_INTERVAL_MILLIS = AUTOMATIC_CHECK_INTERVAL_MILLIS

        internal fun shouldRunAutomaticCheck(lastCheckAtMillis: Long, nowMillis: Long): Boolean =
            lastCheckAtMillis <= 0L || nowMillis - lastCheckAtMillis >= AUTOMATIC_CHECK_INTERVAL_MILLIS
    }
}

private object UnavailableUpdateProvider : UpdateProvider {
    override val source: UpdateSource = UpdateSource.GITEE
    override fun fetchLatest(): UpdateRelease =
        throw UpdateProviderException("Gitee 更新源暂不可用")
}
