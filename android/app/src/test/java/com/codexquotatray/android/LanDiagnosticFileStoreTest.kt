package com.codexquotatray.android

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors

class LanDiagnosticFileStoreTest {
    private lateinit var root: File

    @Before
    fun setUp() {
        root = Files.createTempDirectory("lan-diagnostic-test").toFile()
    }

    @After
    fun tearDown() {
        root.deleteRecursively()
    }

    @Test
    fun migratesLegacyEntriesOnceAndClearsSource() {
        var legacy = "old-1\nold-2"
        val store = store(LegacyLanLogSource { clear ->
            legacy.also { if (clear) legacy = "" }
        })

        assertEquals("old-1\nold-2", store.read())
        assertEquals("", legacy)

        legacy = "must-not-return"
        val restarted = LanDiagnosticFileStore(root, LegacyLanLogSource { legacy }, 3, 1_024)
        assertEquals("old-1\nold-2", restarted.read())
    }

    @Test
    fun rotatesWithinThreeSlotsAndPreservesChronologicalOrder() {
        val store = store(slotCount = 3, maxSlotBytes = 8)
        (1..7).forEach { store.append("e$it") }

        assertEquals("e3\ne4\ne5\ne6\ne7", store.read())
        assertTrue(root.listFiles { file -> file.name.endsWith(".log") }!!.sumOf(File::length) <= 24L)
    }

    @Test
    fun concurrentAppendIsSerializedWithoutCorruption() {
        val store = store(maxSlotBytes = 64 * 1_024)
        val executor = Executors.newFixedThreadPool(8)
        val start = CountDownLatch(1)
        val done = CountDownLatch(200)
        repeat(200) { index ->
            executor.execute {
                start.await()
                LanDiagnosticWriter.append(store, "entry-$index")
                done.countDown()
            }
        }
        start.countDown()
        done.await()
        executor.shutdown()

        val lines = LanDiagnosticWriter.read(store).lineSequence().toList()
        assertEquals(200, lines.size)
        assertEquals(200, lines.toSet().size)
    }

    @Test
    fun clearRemovesAllSlotsAndAllowsOrderedReuse() {
        val store = store(maxSlotBytes = 8)
        (1..5).forEach { store.append("e$it") }

        store.clear()
        assertEquals("", store.read())
        assertFalse(root.listFiles { file -> file.name.endsWith(".log") }!!.any { it.length() > 0L })

        store.append("after-1")
        store.append("after-2")
        assertEquals("after-1\nafter-2", store.read())
    }

    private fun store(
        legacySource: LegacyLanLogSource = LegacyLanLogSource { "" },
        slotCount: Int = 3,
        maxSlotBytes: Int = 1_024,
    ) = LanDiagnosticFileStore(root, legacySource, slotCount, maxSlotBytes)
}
