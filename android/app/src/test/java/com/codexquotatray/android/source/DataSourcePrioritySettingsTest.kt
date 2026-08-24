package com.codexquotatray.android.source

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DataSourcePrioritySettingsTest {
    @Test
    fun defaultsAndIndependentPersistenceAreStable() {
        val store = MemoryStore()
        assertEquals(DataSourcePriority.OPENAI_FIRST, store.load().quota)
        assertEquals(DataSourcePriority.WINDOWS_FIRST, store.load().token)

        store.save(store.load().copy(quota = DataSourcePriority.WINDOWS_FIRST))
        assertEquals(DataSourcePriority.WINDOWS_FIRST, store.load().quota)
        assertEquals(DataSourcePriority.WINDOWS_FIRST, store.load().token)

        store.save(store.load().copy(token = DataSourcePriority.OPENAI_FIRST))
        assertEquals(DataSourcePriority.WINDOWS_FIRST, store.load().quota)
        assertEquals(DataSourcePriority.OPENAI_FIRST, store.load().token)
    }

    @Test
    fun priorityObserverOnlyReportsAnObservedChange() {
        assertFalse(sourcePriorityChanged(null, DataSourcePriority.OPENAI_FIRST))
        assertFalse(
            sourcePriorityChanged(
                DataSourcePriority.OPENAI_FIRST,
                DataSourcePriority.OPENAI_FIRST,
            ),
        )
        assertTrue(
            sourcePriorityChanged(
                DataSourcePriority.OPENAI_FIRST,
                DataSourcePriority.WINDOWS_FIRST,
            ),
        )
    }

    private class MemoryStore : DataSourcePriorityStore {
        private var value = DataSourcePrioritySettings()
        override fun load() = value
        override fun save(value: DataSourcePrioritySettings): Boolean {
            this.value = value
            return true
        }
    }
}
