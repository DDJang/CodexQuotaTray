package com.codexquotatray.android

import android.os.Handler
import android.os.Looper
import com.codexquotatray.android.refresh.AutomaticRefreshReason
import java.util.concurrent.CopyOnWriteArraySet

internal interface ForegroundStopScheduler {
    fun schedule(task: () -> Unit)
    fun cancel()
}

private class HandlerForegroundStopScheduler(
    private val handler: Handler = Handler(Looper.getMainLooper()),
    private val delayMillis: Long = 500L,
) : ForegroundStopScheduler {
    private var pending: Runnable? = null

    override fun schedule(task: () -> Unit) {
        cancel()
        val runnable = Runnable {
            pending = null
            task()
        }
        pending = runnable
        handler.postDelayed(runnable, delayMillis)
    }

    override fun cancel() {
        pending?.let(handler::removeCallbacks)
        pending = null
    }
}

/**
 * Platform-independent process foreground tracker used by the Application.
 * Activity start/stop counts avoid treating navigation between activities as
 * a background/foreground transition. A short stop debounce also covers the
 * stop/start gap produced by Activity recreation and configuration changes.
 */
internal class ProcessForegroundTracker(
    private val stopScheduler: ForegroundStopScheduler = HandlerForegroundStopScheduler(),
) {
    private val listeners = CopyOnWriteArraySet<(AutomaticRefreshReason) -> Unit>()
    private var startedActivityCount = 0
    private var foreground = false
    private var startupObserved = false
    private var pendingReason: AutomaticRefreshReason? = null

    fun addListener(listener: (AutomaticRefreshReason) -> Unit): AutoCloseable {
        listeners += listener
        val pending = synchronized(this) {
            if (foreground && startedActivityCount > 0 && pendingReason != null) {
                pendingReason.also { pendingReason = null }
            } else {
                null
            }
        }
        pending?.let(listener)
        return AutoCloseable { listeners -= listener }
    }

    fun onActivityStarted() {
        stopScheduler.cancel()
        val reason = synchronized(this) {
            startedActivityCount += 1
            if (startedActivityCount != 1 || foreground) {
                null
            } else {
                foreground = true
                val nextReason = if (startupObserved) {
                    AutomaticRefreshReason.FOREGROUND
                } else {
                    startupObserved = true
                    AutomaticRefreshReason.STARTUP
                }
                if (listeners.isEmpty()) pendingReason = nextReason
                nextReason
            }
        }
        reason?.let { event -> listeners.forEach { it(event) } }
    }

    fun onActivityStopped() {
        val shouldSchedule = synchronized(this) {
            startedActivityCount = (startedActivityCount - 1).coerceAtLeast(0)
            startedActivityCount == 0
        }
        if (shouldSchedule) {
            stopScheduler.schedule {
                synchronized(this) {
                    if (startedActivityCount == 0) {
                        foreground = false
                        pendingReason = null
                    }
                }
            }
        }
    }
}
