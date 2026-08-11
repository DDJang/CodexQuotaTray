package com.codexquotatray.android

import com.codexquotatray.android.refresh.AutomaticRefreshReason
import org.junit.Assert.assertEquals
import org.junit.Test

class ProcessForegroundTrackerTest {
    private class TestStopScheduler : ForegroundStopScheduler {
        private var pending: (() -> Unit)? = null

        override fun schedule(task: () -> Unit) {
            pending = task
        }

        override fun cancel() {
            pending = null
        }

        fun runPending() {
            val task = pending
            pending = null
            task?.invoke()
        }
    }

    @Test
    fun startupCanBeDeliveredWhenActivityStartedBeforeListenerRegistration() {
        val tracker = ProcessForegroundTracker(TestStopScheduler())
        val reasons = mutableListOf<AutomaticRefreshReason>()
        tracker.onActivityStarted()
        tracker.addListener(reasons::add)

        assertEquals(listOf(AutomaticRefreshReason.STARTUP), reasons)
    }

    @Test
    fun activityNavigationDoesNotEmitAnotherForegroundEvent() {
        val tracker = ProcessForegroundTracker(TestStopScheduler())
        val reasons = mutableListOf<AutomaticRefreshReason>()
        tracker.addListener(reasons::add)
        tracker.onActivityStarted()
        tracker.onActivityStarted()
        tracker.onActivityStopped()

        assertEquals(listOf(AutomaticRefreshReason.STARTUP), reasons)
    }

    @Test
    fun onlyTransitionFromZeroStartedActivitiesEmitsForeground() {
        val scheduler = TestStopScheduler()
        val tracker = ProcessForegroundTracker(scheduler)
        val reasons = mutableListOf<AutomaticRefreshReason>()
        tracker.addListener(reasons::add)
        tracker.onActivityStarted()
        tracker.onActivityStopped()
        scheduler.runPending()
        tracker.onActivityStarted()

        assertEquals(
            listOf(AutomaticRefreshReason.STARTUP, AutomaticRefreshReason.FOREGROUND),
            reasons,
        )
    }

    @Test
    fun listenerRecreatedWhileForegroundDoesNotRepeatRefresh() {
        val tracker = ProcessForegroundTracker(TestStopScheduler())
        val first = mutableListOf<AutomaticRefreshReason>()
        val registration = tracker.addListener(first::add)
        tracker.onActivityStarted()
        registration.close()

        val second = mutableListOf<AutomaticRefreshReason>()
        tracker.addListener(second::add)

        assertEquals(listOf(AutomaticRefreshReason.STARTUP), first)
        assertEquals(emptyList<AutomaticRefreshReason>(), second)
    }

    @Test
    fun activityRecreationStopStartGapDoesNotEmitForeground() {
        val scheduler = TestStopScheduler()
        val tracker = ProcessForegroundTracker(scheduler)
        val reasons = mutableListOf<AutomaticRefreshReason>()
        tracker.addListener(reasons::add)
        tracker.onActivityStarted()
        tracker.onActivityStopped()
        tracker.onActivityStarted()
        scheduler.runPending()

        assertEquals(listOf(AutomaticRefreshReason.STARTUP), reasons)
    }
}
