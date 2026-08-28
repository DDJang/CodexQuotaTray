package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class LauncherIconResourceTest {
    @Test
    fun adaptiveLauncherIconsKeepAllLayersAndSharedGeometry() {
        val launcher = resourceText("mipmap-anydpi/ic_launcher.xml")
        val round = resourceText("mipmap-anydpi/ic_launcher_round.xml")

        listOf(launcher, round).forEach { source ->
            assertTrue(source.contains("@color/launcher_background"))
            assertTrue(source.contains("@drawable/ic_launcher_foreground"))
            assertTrue(source.contains("<background"))
            assertTrue(source.contains("<foreground"))
            assertTrue(source.contains("<monochrome"))
            assertTrue(source.contains("<monochrome android:drawable=\"@drawable/ic_launcher_foreground\" />"))
        }
    }

    @Test
    fun foregroundUsesClean108DpVectorGeometryAtTheSmallerTargetSize() {
        val foreground = resourceText("drawable/ic_launcher_foreground.xml")

        assertTrue(foreground.contains("<vector"))
        assertTrue(foreground.contains("android:width=\"108dp\""))
        assertTrue(foreground.contains("android:height=\"108dp\""))
        assertTrue(foreground.contains("android:viewportWidth=\"108\""))
        assertTrue(foreground.contains("android:viewportHeight=\"108\""))
        assertTrue(foreground.contains("approximately 55dp x 50dp"))
        assertTrue(foreground.contains("android:strokeColor=\"#FFE6E6E6\""))
        assertTrue(foreground.contains("android:strokeWidth=\"5.5\""))
        assertTrue(foreground.contains("android:strokeLineCap=\"round\""))
        assertTrue(foreground.contains("android:strokeLineJoin=\"round\""))
        assertTrue(foreground.contains("android:fillType=\"evenOdd\""))
        assertTrue(foreground.contains("android:pathData=\"M38.8,75.75"))
        assertTrue(foreground.contains("C29.25,41.2 40.3,31.25 54,31.25"))
        assertTrue(foreground.contains("M54,53.5"))
        assertTrue(foreground.contains("M57.2,56.1 L66.3,47"))
        assertFalse(foreground.contains("<inset"))
        assertFalse(foreground.contains("<bitmap"))
        assertFalse(foreground.contains("android:scaleX"))
        assertFalse(foreground.contains("android:translateX"))
        assertFalse(foreground.contains("M19.5,120"))

        val lineCommands = Regex("""(?<![A-Za-z])L(?=[0-9.-])""")
            .findAll(foreground)
            .count()
        assertTrue("the geometry must not be a raster outline trace", lineCommands <= 2)
    }

    @Test
    fun splashIsIndependentButUsesTheSameCleanGeometry() {
        val styles = resourceText("values/styles.xml")
        val foreground = resourceText("drawable/ic_launcher_foreground.xml")
        val splash = resourceText("drawable/ic_launcher_splash.xml")

        assertTrue(styles.contains("@drawable/ic_launcher_splash"))
        assertTrue(splash.contains("<vector"))
        assertTrue(splash.contains("android:width=\"108dp\""))
        assertTrue(splash.contains("android:height=\"108dp\""))
        assertFalse(splash.contains("<inset"))
        assertFalse(splash.contains("<bitmap"))
        assertEquals(vectorBody(foreground), vectorBody(splash))
    }

    @Test
    fun manifestKeepsAdaptiveLauncherReferences() {
        val manifest = mainSourceFile("AndroidManifest.xml").readText()

        assertTrue(manifest.contains("android:icon=\"@mipmap/ic_launcher\""))
        assertTrue(manifest.contains("android:roundIcon=\"@mipmap/ic_launcher_round\""))
    }

    private fun vectorBody(source: String): String =
        source.substringAfter("<vector").substringBeforeLast("</vector>")
            .replace(Regex("<!--.*?-->", setOf(RegexOption.DOT_MATCHES_ALL)), "")
            .trim()

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
