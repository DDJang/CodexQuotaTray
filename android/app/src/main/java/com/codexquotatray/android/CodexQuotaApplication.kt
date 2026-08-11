package com.codexquotatray.android

import android.app.Activity
import android.app.Application
import android.os.Bundle
import com.codexquotatray.android.refresh.AutomaticRefreshReason

class CodexQuotaApplication : Application(), Application.ActivityLifecycleCallbacks {
    private val foregroundTracker = ProcessForegroundTracker()

    override fun onCreate() {
        super.onCreate()
        registerActivityLifecycleCallbacks(this)
    }

    internal fun registerForegroundListener(
        listener: (AutomaticRefreshReason) -> Unit,
    ): AutoCloseable = foregroundTracker.addListener(listener)

    override fun onActivityStarted(activity: Activity) = foregroundTracker.onActivityStarted()

    override fun onActivityStopped(activity: Activity) = foregroundTracker.onActivityStopped()

    override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit

    override fun onActivityResumed(activity: Activity) = Unit

    override fun onActivityPaused(activity: Activity) = Unit

    override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit

    override fun onActivityDestroyed(activity: Activity) = Unit
}
