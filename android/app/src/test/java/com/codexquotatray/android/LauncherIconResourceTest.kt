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
            assertTrue(source.contains("<monochrome android:drawable=\"@drawable/ic_launcher_foreground\" />"))
        }
    }

    @Test
    fun adaptiveLayersUseOne108DpVectorGeometryWithoutNestedInsets() {
        val foreground = resourceText("drawable/ic_launcher_foreground.xml")

        assertTrue(foreground.contains("android:width=\"108dp\""))
        assertTrue(foreground.contains("android:height=\"108dp\""))
        assertTrue(foreground.contains("android:viewportWidth=\"108\""))
        assertTrue(foreground.contains("android:viewportHeight=\"108\""))
        assertTrue(foreground.contains("android:translateX=\"15\""))
        assertTrue(foreground.contains("android:translateY=\"15\""))
        assertTrue(foreground.contains("android:scaleX=\"0.325\""))
        assertTrue(foreground.contains("android:scaleY=\"0.325\""))
        assertTrue(foreground.contains("android:fillType=\"evenOdd\""))
        assertTrue(!foreground.contains("<inset"))
        assertTrue(!foreground.contains("<bitmap"))
    }

    @Test
    fun splashKeepsItsExistingBitmapGeometry() {
        val styles = resourceText("values/styles.xml")
        val splash = resourceText("drawable/ic_launcher_splash.xml")

        assertTrue(styles.contains("@drawable/ic_launcher_splash"))
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
