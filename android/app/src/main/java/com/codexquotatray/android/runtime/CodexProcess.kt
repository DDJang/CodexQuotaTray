package com.codexquotatray.android.runtime

import java.io.InputStream
import java.util.concurrent.atomic.AtomicBoolean

data class ProcessCleanup(
    val succeeded: Boolean,
    val returnCode: Int?,
    val stderrObserved: Boolean,
)

class CodexProcess private constructor(
    private val process: Process,
    private val stderrObserved: AtomicBoolean,
    private val readerThreads: List<Thread>,
) {
    fun isAlive(): Boolean = process.isAlive

    @Synchronized
    fun stop(): ProcessCleanup {
        runCatching { process.inputStream.close() }
        runCatching { process.errorStream.close() }
        runCatching { process.outputStream.close() }

        if (process.isAlive) {
            process.destroy()
            runCatching { process.waitFor(1_000L, java.util.concurrent.TimeUnit.MILLISECONDS) }
        }
        if (process.isAlive) {
            process.destroyForcibly()
            runCatching { process.waitFor(1_000L, java.util.concurrent.TimeUnit.MILLISECONDS) }
        }

        readerThreads.forEach { thread -> runCatching { thread.join(500L) } }
        return ProcessCleanup(
            succeeded = !process.isAlive,
            returnCode = runCatching { process.exitValue() }.getOrNull(),
            stderrObserved = stderrObserved.get(),
        )
    }

    companion object {
        fun start(runtime: EmbeddedCodexRuntime, status: RuntimeStatus, port: Int): CodexProcess {
            val binary = status.codexBinary
                ?: throw IllegalStateException("codex binary is unavailable")
            val process = ProcessBuilder(
                binary.absolutePath,
                "app-server",
                "--listen",
                "ws://127.0.0.1:$port",
            )
                .directory(status.codexHome)
                .redirectErrorStream(false)
                .apply { environment().putAll(runtime.environment(status)) }
                .start()

            val stderrObserved = AtomicBoolean(false)
            val stdoutReader = drainAsync("codex-app-server-stdout", process.inputStream) { }
            val stderrReader = drainAsync("codex-app-server-stderr", process.errorStream) {
                stderrObserved.set(true)
            }
            return CodexProcess(process, stderrObserved, listOf(stdoutReader, stderrReader))
        }

        private fun drainAsync(name: String, stream: InputStream, onBytes: () -> Unit): Thread =
            Thread {
                val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                runCatching {
                    stream.use { input ->
                        while (input.read(buffer) >= 0) {
                            onBytes()
                        }
                    }
                }
            }.apply {
                this.name = name
                isDaemon = true
                start()
            }
    }
}

private const val DEFAULT_BUFFER_SIZE = 4 * 1024
