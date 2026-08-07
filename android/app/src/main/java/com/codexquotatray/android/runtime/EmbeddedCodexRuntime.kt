package com.codexquotatray.android.runtime

import android.content.Context
import android.os.Build
import com.codexquotatray.android.BuildConfig
import java.io.File
import java.util.concurrent.TimeUnit

data class RuntimeStatus(
    val packaged: Boolean,
    val nativeLibraryPresent: Boolean,
    val ready: Boolean,
    val version: String,
    val nativeLibraryDirectory: File,
    val codexHome: File,
    val codexHomeState: File,
    val codexBinary: File?,
    val executableExists: Boolean,
    val executableCanExecute: Boolean,
    val detail: String,
)

data class VersionProbe(
    val succeeded: Boolean,
    val version: String,
    val detail: String,
)

class EmbeddedCodexRuntime(private val context: Context) {
    private val version = BuildConfig.CODEX_RUNTIME_VERSION

    fun nativeLibraryDirectory(): File = File(context.applicationInfo.nativeLibraryDir)

    fun codexHome(): File = File(context.filesDir, "codex-home")

    fun codexHomeState(): File = File(codexHome(), ".codex")

    fun ensureReady(): RuntimeStatus {
        val home = codexHome().apply { mkdirs() }
        val state = codexHomeState().apply { mkdirs() }
        val nativeDir = nativeLibraryDirectory()
        val binary = File(nativeDir, "libcodex_exec.so")
        val executableExists = binary.isFile
        val executableCanExecute = executableExists && binary.canExecute()

        if (!Build.SUPPORTED_ABIS.any { it.equals("arm64-v8a", ignoreCase = true) }) {
            return status(
                packaged = BuildConfig.CODEX_RUNTIME_PACKAGED,
                nativeLibraryPresent = executableExists,
                ready = false,
                detail = "device ABI is not arm64-v8a",
                nativeDir = nativeDir,
                home = home,
                state = state,
                binary = binary.takeIf(File::isFile),
                executableExists = executableExists,
                executableCanExecute = executableCanExecute,
            )
        }

        if (!BuildConfig.CODEX_RUNTIME_PACKAGED) {
            return status(
                packaged = false,
                nativeLibraryPresent = executableExists,
                ready = false,
                detail = "runtime not packaged; set CODEX_ANDROID_RUNTIME or -PcodexAndroid.runtime",
                nativeDir = nativeDir,
                home = home,
                state = state,
                binary = binary.takeIf(File::isFile),
                executableExists = executableExists,
                executableCanExecute = executableCanExecute,
            )
        }

        return status(
            packaged = true,
            nativeLibraryPresent = executableExists,
            ready = executableCanExecute,
            detail = when {
                !executableExists -> "native library missing: ${binary.absolutePath}"
                !executableCanExecute -> "native library is not executable: ${binary.absolutePath}"
                else -> "embedded native runtime ready"
            },
            nativeDir = nativeDir,
            home = home,
            state = state,
            binary = binary.takeIf(File::isFile),
            executableExists = executableExists,
            executableCanExecute = executableCanExecute,
        )
    }

    fun environment(status: RuntimeStatus): Map<String, String> {
        val libraryDirectory = status.nativeLibraryDirectory.absolutePath
        val currentPath = System.getenv("PATH").orEmpty()
        val currentLibraryPath = System.getenv("LD_LIBRARY_PATH").orEmpty()
        return mapOf(
            "HOME" to status.codexHome.absolutePath,
            "CODEX_HOME" to status.codexHomeState.absolutePath,
            "CODEX_SELF_EXE" to (status.codexBinary?.absolutePath ?: ""),
            "PATH" to listOf(libraryDirectory, "/system/bin", currentPath)
                .filter(String::isNotBlank)
                .distinct()
                .joinToString(File.pathSeparator),
            "LD_LIBRARY_PATH" to listOf(libraryDirectory, currentLibraryPath)
                .filter(String::isNotBlank)
                .distinct()
                .joinToString(File.pathSeparator),
        )
    }

    fun probeVersion(status: RuntimeStatus): VersionProbe {
        val binary = status.codexBinary
            ?: return VersionProbe(false, "unavailable", "codex binary unavailable")
        val process = runCatching {
            ProcessBuilder(binary.absolutePath, "--version")
                .directory(status.codexHome)
                .redirectErrorStream(true)
                .apply { environment().putAll(environment(status)) }
                .start()
        }.getOrElse { error ->
            return VersionProbe(
                false,
                "unavailable",
                "codex --version failed: ${error.javaClass.simpleName}: ${error.processMessage()}",
            )
        }

        val output = StringBuilder()
        val reader = Thread {
            process.inputStream.bufferedReader().useLines { lines ->
                for (line in lines) {
                    if (output.length < 512) {
                        output.append(line.take(256)).append('\n')
                    }
                }
            }
        }.apply {
            name = "codex-version-reader"
            isDaemon = true
            start()
        }

        val exited = runCatching { process.waitFor(5, TimeUnit.SECONDS) }.getOrDefault(false)
        if (!exited) {
            process.destroy()
            process.destroyForcibly()
        }
        reader.join(1_000L)

        val match = Regex("(?<!\\d)\\d+\\.\\d+\\.\\d+(?:[-+][A-Za-z0-9.-]+)?")
            .find(output.toString())
            ?.value
        val exitCode = process.exitValueOrNull()
        return if (exited && exitCode == 0 && match != null) {
            VersionProbe(true, match, "codex --version succeeded")
        } else {
            VersionProbe(false, match ?: "unavailable", "codex --version did not succeed")
        }
    }

    private fun status(
        packaged: Boolean,
        nativeLibraryPresent: Boolean,
        ready: Boolean,
        detail: String,
        nativeDir: File,
        home: File,
        state: File,
        binary: File?,
        executableExists: Boolean,
        executableCanExecute: Boolean,
    ): RuntimeStatus = RuntimeStatus(
        packaged = packaged,
        nativeLibraryPresent = nativeLibraryPresent,
        ready = ready,
        version = version,
        nativeLibraryDirectory = nativeDir,
        codexHome = home,
        codexHomeState = state,
        codexBinary = binary,
        executableExists = executableExists,
        executableCanExecute = executableCanExecute,
        detail = detail,
    )
}

private fun Throwable.processMessage(): String =
    message?.replace(Regex("[\\r\\n]+"), " ")?.ifBlank { "no message" } ?: "no message"

private fun Process.exitValueOrNull(): Int? = runCatching { exitValue() }.getOrNull()
