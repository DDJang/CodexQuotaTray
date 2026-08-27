package com.codexquotatray.android

import java.io.File
import java.io.FileOutputStream
import java.nio.charset.StandardCharsets
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

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

internal class BoundedLanDiagnosticWriter(
    queueCapacity: Int,
    private val appendLine: (LanDiagnosticFileStore, String) -> Unit = LanDiagnosticFileStore::append,
) {
    private sealed interface Operation {
        data class Append(
            val store: LanDiagnosticFileStore,
            val line: String,
            val generation: Generation,
        ) : Operation

        data class Read(
            val store: LanDiagnosticFileStore,
            val result: CompletableFuture<String>,
        ) : Operation

        data object Wake : Operation
    }

    private class Generation {
        val droppedCount = AtomicLong()
    }

    private val queue = ArrayBlockingQueue<Operation>(queueCapacity)
    private val submitGate = Any()
    private val generation = AtomicReference(Generation())
    private val pendingClears = ConcurrentHashMap.newKeySet<LanDiagnosticFileStore>()

    init {
        Thread({
            while (true) {
                drainPendingClears()
                val operation = queue.take()
                drainPendingClears()
                runCatching {
                    when (operation) {
                        is Operation.Append -> {
                            if (operation.generation === generation.get()) {
                                appendWithDropSummary(operation)
                            }
                        }
                        is Operation.Read -> runCatching(operation.store::read)
                            .fold(operation.result::complete, operation.result::completeExceptionally)
                        Operation.Wake -> Unit
                    }
                }
            }
        }, "CodexQuotaTray-LAN-log").apply {
            isDaemon = true
            start()
        }
    }

    fun append(store: LanDiagnosticFileStore, line: String) {
        synchronized(submitGate) {
            val currentGeneration = generation.get()
            if (!queue.offer(Operation.Append(store, line, currentGeneration))) {
                currentGeneration.droppedCount.incrementAndGet()
            }
        }
    }

    fun clear(store: LanDiagnosticFileStore) {
        synchronized(submitGate) {
            generation.set(Generation())
            pendingClears += store
            queue.offer(Operation.Wake)
        }
    }

    fun read(store: LanDiagnosticFileStore): String {
        val result = CompletableFuture<String>()
        queue.put(Operation.Read(store, result))
        return result.get()
    }

    internal fun queuedReadCount(): Int = queue.count { it is Operation.Read }

    private fun drainPendingClears() {
        while (true) {
            val store = pendingClears.firstOrNull() ?: return
            if (pendingClears.remove(store)) runCatching(store::clear)
        }
    }

    private fun appendWithDropSummary(operation: Operation.Append) {
        val dropped = operation.generation.droppedCount.get()
        if (dropped > 0L) {
            appendLine(
                operation.store,
                "${AppLogRetention.formatTimestamp(System.currentTimeMillis())} [WARN] " +
                    "LAN diagnostics dropped $dropped entries because the writer queue was full",
            )
            operation.generation.droppedCount.addAndGet(-dropped)
        }
        appendLine(operation.store, operation.line)
    }
}

internal object LanDiagnosticWriter {
    private const val QUEUE_CAPACITY = 256
    private val delegate = BoundedLanDiagnosticWriter(QUEUE_CAPACITY)

    fun append(store: LanDiagnosticFileStore, line: String) = delegate.append(store, line)

    fun clear(store: LanDiagnosticFileStore) = delegate.clear(store)

    fun read(store: LanDiagnosticFileStore): String = delegate.read(store)
}
