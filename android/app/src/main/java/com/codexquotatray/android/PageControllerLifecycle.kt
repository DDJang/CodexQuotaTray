package com.codexquotatray.android

internal class PageControllerLifecycle {
    private var destroyed = false
    private var generation = 0L

    @Synchronized
    fun beginOperation(): Long? {
        if (destroyed) return null
        generation++
        return generation
    }

    @Synchronized
    fun isCurrent(value: Long): Boolean = !destroyed && generation == value

    @Synchronized
    fun isAlive(): Boolean = !destroyed

    @Synchronized
    fun destroy() {
        if (destroyed) return
        destroyed = true
        generation++
    }
}
