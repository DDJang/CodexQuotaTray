package com.codexquotatray.android.refresh

/** The independently coordinated automatic refresh channels. */
internal enum class AutomaticRefreshChannel {
    QUOTA,
    TOKEN,
}

/** Why an operation was requested. Manual actions and source changes bypass the automatic gate. */
internal enum class AutomaticRefreshReason {
    STARTUP,
    FOREGROUND,
    SCHEDULED,
    RETRY,
    MANUAL,
    SOURCE_CHANGED,
}

/**
 * Small process-local gate shared by page and worker entry points.
 *
 * It provides in-flight de-duplication and remembers the last automatic
 * attempt.  The two channels intentionally have independent state so quota
 * and Token Usage settings cannot suppress one another.
 */
internal class AutomaticRefreshCoordinator(
    private val nowMillis: () -> Long = System::currentTimeMillis,
) {
    private data class ChannelState(
        var inFlight: Boolean = false,
        var lastAutomaticAttemptAtMillis: Long? = null,
    )

    private val states = AutomaticRefreshChannel.entries.associateWith { ChannelState() }

    @Synchronized
    fun tryStart(
        channel: AutomaticRefreshChannel,
        reason: AutomaticRefreshReason,
        enabled: Boolean = true,
    ): Boolean {
        val state = states.getValue(channel)
        if (state.inFlight) return false
        if (reason !in setOf(AutomaticRefreshReason.MANUAL, AutomaticRefreshReason.SOURCE_CHANGED) && !enabled) return false

        val now = nowMillis()
        if (reason !in setOf(
                AutomaticRefreshReason.MANUAL,
                AutomaticRefreshReason.RETRY,
                AutomaticRefreshReason.SOURCE_CHANGED,
            ) &&
            !ForegroundRefreshPolicy.shouldRunOnForeground(
                enabled = enabled,
                lastAttemptAtMillis = state.lastAutomaticAttemptAtMillis,
                nowMillis = now,
            )
        ) {
            return false
        }

        if (reason !in setOf(
                AutomaticRefreshReason.MANUAL,
                AutomaticRefreshReason.RETRY,
                AutomaticRefreshReason.SOURCE_CHANGED,
            )
        ) {
            state.lastAutomaticAttemptAtMillis = now
        }
        state.inFlight = true
        return true
    }

    @Synchronized
    fun finish(channel: AutomaticRefreshChannel) {
        states.getValue(channel).inFlight = false
    }

    @Synchronized
    fun lastAutomaticAttemptAtMillis(channel: AutomaticRefreshChannel): Long? =
        states.getValue(channel).lastAutomaticAttemptAtMillis
}

/** Shared coordinator used by the Android process' page and worker paths. */
internal object AppAutomaticRefreshCoordinator {
    private val delegate = AutomaticRefreshCoordinator()

    fun tryStart(
        channel: AutomaticRefreshChannel,
        reason: AutomaticRefreshReason,
        enabled: Boolean = true,
    ): Boolean = delegate.tryStart(channel, reason, enabled)

    fun finish(channel: AutomaticRefreshChannel) = delegate.finish(channel)
}
