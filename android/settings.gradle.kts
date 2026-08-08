pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
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
