import java.io.File

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

fun stringPropertyOrEnv(propertyName: String, envName: String): String? {
    val propertyValue = (project.findProperty(propertyName) as String?)?.trim()
    val envValue = System.getenv(envName)?.trim()
    return propertyValue?.takeIf { it.isNotEmpty() } ?: envValue?.takeIf { it.isNotEmpty() }
}

fun inferredRuntimeVersion(path: String?): String {
    if (path.isNullOrBlank()) return "unpackaged"
    return File(path).name
        .removeSuffix(".tar.gz")
        .removeSuffix(".tgz")
        .removeSuffix(".zip")
        .ifBlank { "embedded-dev" }
        .replace(Regex("[^A-Za-z0-9._-]"), "_")
}

fun buildConfigString(value: String): String =
    "\"${value.replace("\\", "\\\\").replace("\"", "\\\"")}\""

fun isElf(file: File): Boolean {
    if (!file.isFile || file.length() < 4) return false
    return runCatching {
        file.inputStream().use { input ->
            input.read() == 0x7f && input.read() == 0x45 && input.read() == 0x4c && input.read() == 0x46
        }
    }.getOrDefault(false)
}

val runtimeInputPath = stringPropertyOrEnv("codexAndroid.runtime", "CODEX_ANDROID_RUNTIME")
val runtimeInput = runtimeInputPath?.let(::file)
if (runtimeInputPath != null && runtimeInput?.exists() != true) {
    throw GradleException("Codex Android runtime input does not exist: $runtimeInputPath")
}

val runtimeVersion =
    stringPropertyOrEnv("codexAndroid.runtimeVersion", "CODEX_ANDROID_RUNTIME_VERSION")
        ?.replace(Regex("[^A-Za-z0-9._-]"), "_")
        ?.takeIf { it.isNotBlank() }
        ?: inferredRuntimeVersion(runtimeInputPath)

val appServerPort =
    (stringPropertyOrEnv("codexAndroid.port", "CODEX_ANDROID_PORT") ?: "43128").toIntOrNull()
        ?.takeIf { it in 1024..65535 }
        ?: throw GradleException("codexAndroid.port must be an integer between 1024 and 65535")

val generatedNativeLibsDir = layout.buildDirectory.dir("generated/codexNativeLibs")

val prepareCodexNativeLibs by tasks.registering {
    outputs.dir(generatedNativeLibsDir)
    runtimeInput?.let { source ->
        if (source.isDirectory) inputs.dir(source) else inputs.file(source)
    }

    doLast {
        val outputRoot = generatedNativeLibsDir.get().asFile
        outputRoot.deleteRecursively()
        val arm64Libs = outputRoot.resolve("arm64-v8a")
        arm64Libs.mkdirs()

        val source = runtimeInput
        if (source == null) {
            println("Codex Android runtime: not packaged (diagnostic APK only)")
            return@doLast
        }

        val staging = temporaryDir.resolve("codex-runtime-staging").apply {
            deleteRecursively()
            mkdirs()
        }

        when {
            source.isDirectory -> copy {
                from(source)
                into(staging)
            }

            source.name.endsWith(".zip", ignoreCase = true) -> copy {
                from(zipTree(source))
                into(staging)
            }

            source.name.endsWith(".tar.gz", ignoreCase = true) ||
                source.name.endsWith(".tgz", ignoreCase = true) -> copy {
                from(tarTree(resources.gzip(source)))
                into(staging)
            }

            else -> throw GradleException(
                "Unsupported Codex Android runtime input; use a directory, .zip, .tar.gz or .tgz: ${source.absolutePath}",
            )
        }

        val nativeBinary = staging.walkTopDown()
            .filter { it.isFile && (it.name == "codex.bin" || it.name == "codex") }
            .firstOrNull(::isElf)
            ?: throw GradleException(
                "Runtime input has no native ELF codex.bin/codex. Do not package only codex.js or the shell launcher.",
            )
        val sharedLibraries = staging.walkTopDown()
            .filter { it.isFile && it.name == "libc++_shared.so" }
            .toList()
        val libcxx = sharedLibraries.firstOrNull()
            ?: throw GradleException("Runtime input has no libc++_shared.so")
        val nativeOutput = arm64Libs.resolve("libcodex_exec.so")
        nativeBinary.copyTo(nativeOutput, overwrite = true)
        libcxx.copyTo(arm64Libs.resolve("libc++_shared.so"), overwrite = true)

        println("Codex Android runtime input: ${source.absolutePath}")
        println("Codex Android runtime version: $runtimeVersion")
        println("Codex Android native executable: ${nativeBinary.relativeTo(staging).invariantSeparatorsPath}")
        println("Codex Android native library: ${nativeOutput.absolutePath}")
        println("Codex Android shared library: ${libcxx.relativeTo(staging).invariantSeparatorsPath}")
    }
}

android {
    namespace = "com.codexquotatray.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.codexquotatray.android"
        minSdk = 29
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0-p0.5"

        ndk {
            abiFilters += "arm64-v8a"
        }

        buildConfigField("boolean", "CODEX_RUNTIME_PACKAGED", (runtimeInput != null).toString())
        buildConfigField("String", "CODEX_RUNTIME_VERSION", buildConfigString(runtimeVersion))
        buildConfigField("int", "CODEX_APP_SERVER_PORT", appServerPort.toString())
    }

    buildFeatures {
        buildConfig = true
    }

    sourceSets.getByName("main").jniLibs.srcDir(generatedNativeLibsDir.get().asFile)

    packaging {
        jniLibs {
            useLegacyPackaging = true
            keepDebugSymbols.add("**/libcodex_exec.so")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

tasks.named("preBuild") {
    dependsOn(prepareCodexNativeLibs)
}

dependencies {
    // Android has no platform WebSocket client; this is the only P0.5 runtime dependency.
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    testImplementation("junit:junit:4.13.2")
}
