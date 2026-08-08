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

dependencies {
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("androidx.work:work-runtime:2.9.1")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
}
