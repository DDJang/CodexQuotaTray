pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }

    buildscript {
        repositories {
            google()
            mavenCentral()
        }
        dependencies {
            classpath("com.android.tools:r8:8.13.19")
        }
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(
        org.gradle.api.initialization.resolve.RepositoriesMode.FAIL_ON_PROJECT_REPOS,
    )
    repositories {
        google()
        mavenCentral()
        maven("https://jitpack.io")
    }
}

rootProject.name = "CodexQuotaTrayAndroid"
include(":app")
