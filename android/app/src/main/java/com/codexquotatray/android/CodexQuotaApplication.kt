package com.codexquotatray.android

import android.app.Activity
import android.app.Application
import android.os.Bundle
import com.codexquotatray.android.refresh.AutomaticRefreshReason
import com.codexquotatray.android.update.UpdateCheckCoordinator
import com.codexquotatray.android.update.UpdateDownloadManager
import com.codexquotatray.android.update.UpdateRelease
import com.codexquotatray.android.usage.AndroidLanNetworkLifecycle

class CodexQuotaApplication : Application(), Application.ActivityLifecycleCallbacks {
    private val foregroundTracker by lazy { ProcessForegroundTracker() }
    val updateCheckCoordinator: UpdateCheckCoordinator by lazy { UpdateCheckCoordinator(this) }
    val updateDownloadManager: UpdateDownloadManager by lazy { UpdateDownloadManager(this) }
    internal val lanNetworkLifecycle: AndroidLanNetworkLifecycle by lazy {
        AndroidLanNetworkLifecycle(this)
    }

    override fun onCreate() {
        super.onCreate()
        if (isWidgetProcessName(Application.getProcessName())) return
        registerActivityLifecycleCallbacks(this)
        lanNetworkLifecycle.start()
        updateDownloadManager.cleanupStaleFiles()
        com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(this)
    }

    internal fun registerForegroundListener(
        listener: (AutomaticRefreshReason) -> Unit,
    ): AutoCloseable = foregroundTracker.addListener(listener)

    internal fun registerUpdateReminderListener(
        listener: (UpdateRelease) -> Unit,
    ): AutoCloseable = updateCheckCoordinator.addReminderListener(listener)

    override fun onActivityStarted(activity: Activity) = foregroundTracker.onActivityStarted()

    override fun onActivityStopped(activity: Activity) = foregroundTracker.onActivityStopped()

    override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit

    override fun onActivityResumed(activity: Activity) = Unit

    override fun onActivityPaused(activity: Activity) = Unit

    override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit

    override fun onActivityDestroyed(activity: Activity) = Unit
}

internal fun isWidgetProcessName(processName: String?): Boolean =
    processName?.endsWith(":widgetProvider") == true
