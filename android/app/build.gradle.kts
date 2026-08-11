import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
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
        versionCode = 4
        versionName = "0.6.6"
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
        getByName("debug") {
            applicationIdSuffix = ".debug"
        }

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

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("org.jetbrains.compose.animation:animation:1.11.0")
    implementation("org.jetbrains.compose.foundation:foundation:1.11.0")
    implementation("org.jetbrains.compose.ui:ui:1.11.0")
    implementation("androidx.compose.material3:material3:1.4.0")
    implementation("io.github.kyant0:backdrop:2.0.0")
    implementation("io.github.kyant0:shapes:1.2.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("androidx.work:work-runtime:2.9.1")
    implementation("androidx.core:core-ktx:1.13.1")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
}
