package com.codexquotatray.android

import java.io.File
import java.io.FileOutputStream
import java.nio.charset.StandardCharsets
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CompletableFuture

internal fun interface LegacyLanLogSource {
    fun loadAndClear(clear: Boolean): String
}

internal class LanDiagnosticFileStore(
    private val directory: File,
    private val legacySource: LegacyLanLogSource,
    private val slotCount: Int = AppLanLogRetention.SLOT_COUNT,
    private val maxSlotBytes: Int = AppLanLogRetention.MAX_SLOT_BYTES,
) {
    private var initialized = false
    private var sequence = 0L
    private var currentSize = 0L

    fun append(line: String) {
        ensureInitialized()
        appendBytes((line + "\n").toByteArray(StandardCharsets.UTF_8))
    }

    fun read(): String {
        ensureInitialized()
        val firstSequence = maxOf(0L, sequence - slotCount + 1L)
        return (firstSequence..sequence)
            .map(::slotFileForSequence)
            .filter(File::isFile)
            .joinToString(separator = "") { it.readText(StandardCharsets.UTF_8) }
            .trimEnd('\r', '\n')
    }

    fun clear() {
        ensureInitialized()
        repeat(slotCount) { slotFile(it).delete() }
        stateFile.delete()
        sequence = 0L
        currentSize = 0L
        writeState()
        legacySource.loadAndClear(true)
    }

    private fun ensureInitialized() {
        if (initialized) return
        directory.mkdirs()
        if (!migrationMarker.isFile) {
            repeat(slotCount) { slotFile(it).delete() }
            stateFile.delete()
            sequence = 0L
            currentSize = 0L
            val legacy = legacySource.loadAndClear(false)
            legacy.lineSequence().filter(String::isNotBlank).forEach(::appendMigratedLine)
            writeState()
            migrationMarker.writeText("1", StandardCharsets.US_ASCII)
            legacySource.loadAndClear(true)
        } else {
            sequence = stateFile.takeIf(File::isFile)?.readText(StandardCharsets.US_ASCII)
                ?.trim()?.toLongOrNull()?.coerceAtLeast(0L) ?: 0L
            currentSize = slotFileForSequence(sequence).takeIf(File::isFile)?.length() ?: 0L
            legacySource.loadAndClear(true)
        }
        initialized = true
    }

    private fun appendMigratedLine(line: String) {
        appendBytes((line + "\n").toByteArray(StandardCharsets.UTF_8))
    }

    private fun appendBytes(bytes: ByteArray) {
        val bounded = if (bytes.size <= maxSlotBytes) bytes else bytes.copyOf(maxSlotBytes)
        if (currentSize > 0L && currentSize + bounded.size > maxSlotBytes) {
            sequence++
            FileOutputStream(slotFileForSequence(sequence), false).use { }
            currentSize = 0L
            writeState()
        }
        FileOutputStream(slotFileForSequence(sequence), true).use { output -> output.write(bounded) }
        currentSize += bounded.size
    }

    private fun writeState() {
        val temporary = File(directory, "$STATE_FILE.tmp")
        temporary.writeText(sequence.toString(), StandardCharsets.US_ASCII)
        if (!temporary.renameTo(stateFile)) {
            stateFile.writeText(sequence.toString(), StandardCharsets.US_ASCII)
            temporary.delete()
        }
    }

    private fun slotFileForSequence(value: Long): File = slotFile((value % slotCount).toInt())
    private fun slotFile(index: Int): File = File(directory, "lan-$index.log")
    private val stateFile: File get() = File(directory, STATE_FILE)
    private val migrationMarker: File get() = File(directory, MIGRATION_MARKER)

    private companion object {
        const val STATE_FILE = "state"
        const val MIGRATION_MARKER = "preferences-migrated"
    }
}

internal object LanDiagnosticWriter {
    private const val QUEUE_CAPACITY = 256
    private val queue = ArrayBlockingQueue<() -> Unit>(QUEUE_CAPACITY)
    private val submitGate = Any()

    init {
        Thread({
            while (true) runCatching { queue.take().invoke() }
        }, "CodexQuotaTray-LAN-log").apply {
            isDaemon = true
            start()
        }
    }

    fun append(store: LanDiagnosticFileStore, line: String) {
        synchronized(submitGate) { queue.offer { store.append(line) } }
    }

    fun clear(store: LanDiagnosticFileStore) {
        synchronized(submitGate) {
            queue.clear()
            queue.offer { store.clear() }
        }
    }

    fun read(store: LanDiagnosticFileStore): String {
        val result = CompletableFuture<String>()
        synchronized(submitGate) {
            queue.put {
                runCatching(store::read).fold(result::complete, result::completeExceptionally)
            }
        }
        return result.get()
    }
}
