package com.codexquotatray.android

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class LauncherIconResourceTest {
    @Test
    fun adaptiveLauncherIconsShareCompleteLayerGeometry() {
        val launcher = resourceText("mipmap-anydpi/ic_launcher.xml")
        val round = resourceText("mipmap-anydpi/ic_launcher_round.xml")

        listOf(launcher, round).forEach { source ->
            assertTrue(source.contains("@color/launcher_background"))
            assertTrue(source.contains("@drawable/ic_launcher_foreground"))
            assertTrue(source.contains("@drawable/ic_launcher_monochrome"))
        }
    }

    @Test
    fun adaptiveLayersUseSharedSafeGeometryWithoutTheOldInset() {
        val foreground = resourceText("drawable/ic_launcher_foreground.xml")
        val monochrome = resourceText("drawable/ic_launcher_monochrome.xml")

        listOf(foreground, monochrome).forEach { source ->
            assertTrue(source.contains("@drawable/ic_launcher_mark"))
            assertTrue(source.contains("android:inset=\"15dp\""))
            assertTrue(!source.contains("18dp"))
        }
        assertTrue(monochrome.contains("android:tint=\"@android:color/white\""))
    }

    @Test
    fun splashKeepsItsPreviousGeometryWithoutSharingAdaptiveForeground() {
        val styles = resourceText("values/styles.xml")
        val splash = resourceText("drawable/ic_launcher_splash.xml")

        assertTrue(styles.contains("@drawable/ic_launcher_splash"))
        assertTrue(!styles.contains("@drawable/ic_launcher_foreground</item>"))
        assertTrue(splash.contains("@drawable/ic_launcher_mark"))
        assertTrue(splash.contains("android:inset=\"18dp\""))
    }

    @Test
    fun manifestKeepsAdaptiveLauncherReferences() {
        val manifest = mainSourceFile("AndroidManifest.xml").readText()

        assertTrue(manifest.contains("android:icon=\"@mipmap/ic_launcher\""))
        assertTrue(manifest.contains("android:roundIcon=\"@mipmap/ic_launcher_round\""))
    }

    private fun resourceText(relative: String): String = resourceFile(relative).readText()

    private fun resourceFile(relative: String): File =
        mainSourceFile("res/$relative").also {
            require(it.isFile) { "$relative not found from ${System.getProperty("user.dir")}" }
        }

    private fun mainSourceFile(relative: String): File =
        listOf(
            File("android/app/src/main/$relative"),
            File("app/src/main/$relative"),
            File("src/main/$relative"),
        ).firstOrNull(File::isFile)
            ?: error("$relative not found from ${System.getProperty("user.dir")}")
}
