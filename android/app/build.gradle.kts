plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

fun rawPropertyOrEnv(propertyName: String, envName: String): String? {
    val propertyValue = project.findProperty(propertyName) as String?
    val envValue = System.getenv(envName)
    return propertyValue?.takeIf { it.isNotEmpty() } ?: envValue?.takeIf { it.isNotEmpty() }
}

val releaseKeystorePath = rawPropertyOrEnv(
    "codexAndroid.releaseKeystore",
    "CODEX_ANDROID_RELEASE_KEYSTORE",
)
val releaseStorePassword = rawPropertyOrEnv(
    "codexAndroid.releaseStorePassword",
    "CODEX_ANDROID_RELEASE_STORE_PASSWORD",
)
val releaseKeyAlias = rawPropertyOrEnv(
    "codexAndroid.releaseKeyAlias",
    "CODEX_ANDROID_RELEASE_KEY_ALIAS",
)
val releaseKeyPassword = rawPropertyOrEnv(
    "codexAndroid.releaseKeyPassword",
    "CODEX_ANDROID_RELEASE_KEY_PASSWORD",
)
val releaseSigningValues = listOf(
    releaseKeystorePath,
    releaseStorePassword,
    releaseKeyAlias,
    releaseKeyPassword,
)
val releaseSigningRequested = releaseSigningValues.any { it != null }
val releaseSigningConfigured = releaseSigningValues.all { !it.isNullOrEmpty() }
if (releaseSigningRequested && !releaseSigningConfigured) {
    throw GradleException(
        "Release signing configuration is incomplete; provide keystore, store password, key alias, and key password.",
    )
}
if (releaseSigningConfigured && !file(releaseKeystorePath!!).isFile) {
    throw GradleException("Release keystore does not exist: $releaseKeystorePath")
}

android {
    namespace = "com.codexquotatray.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.codexquotatray.android"
        minSdk = 29
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"
    }

    signingConfigs {
        if (releaseSigningConfigured) {
            create("release") {
                storeFile = file(releaseKeystorePath!!)
                storePassword = releaseStorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        getByName("release") {
            isMinifyEnabled = false
            if (releaseSigningConfigured) {
                signingConfig = signingConfigs.getByName("release")
            }
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

// Keep the repository's Kotlin 2.0.21 compiler/runtime boundary intact. Some
// AndroidX metadata in the local repository advertises a newer stdlib, while
// the app and the Liquid Glass Java-compatible API do not need it.
configurations.configureEach {
    resolutionStrategy.force(
        "org.jetbrains.kotlin:kotlin-stdlib:2.0.21",
        "org.jetbrains.kotlin:kotlin-stdlib-common:2.0.21",
    )
}

dependencies {
    implementation("com.qmdeve.liquidglass:core:1.0.5") {
        // The library is compiled with Kotlin 2.2.x. Keep the app's existing
        // Kotlin 2.0.21 runtime/toolchain; the public Java-compatible API does
        // not require the newer stdlib at runtime.
        exclude(group = "org.jetbrains.kotlin", module = "kotlin-stdlib")
    }
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("com.github.Dimezis:BlurView:version-3.2.0")
    implementation("androidx.work:work-runtime:2.9.1")
    implementation("androidx.core:core-ktx:1.13.1")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
}
