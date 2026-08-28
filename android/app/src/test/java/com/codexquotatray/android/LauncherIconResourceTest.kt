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
            assertTrue(source.contains("<background"))
            assertTrue(source.contains("@color/launcher_background"))
            assertTrue(source.contains("<foreground"))
            assertTrue(source.contains("@drawable/ic_launcher_foreground"))
            assertTrue(source.contains("<monochrome"))
            assertTrue(source.contains("<monochrome android:drawable=\"@drawable/ic_launcher_foreground\" />"))
        }
    }

    @Test
    fun foregroundPreservesDashboardSvgGeometryWithUniformScale() {
        val foreground = resourceText("drawable/ic_launcher_foreground.xml")
        val compact = foreground.replace(Regex("\\s+"), "")

        assertTrue(foreground.contains("<vector"))
        assertTrue(foreground.contains("android:width=\"108dp\""))
        assertTrue(foreground.contains("android:height=\"108dp\""))
        assertTrue(foreground.contains("android:viewportWidth=\"24\""))
        assertTrue(foreground.contains("android:viewportHeight=\"24\""))
        assertTrue(foreground.contains("android:pivotX=\"12\""))
        assertTrue(foreground.contains("android:pivotY=\"12\""))
        assertTrue(foreground.contains("android:scaleX=\"0.61\""))
        assertTrue(foreground.contains("android:scaleY=\"0.61\""))
        assertTrue(foreground.contains("android:strokeWidth=\"2\""))
        assertTrue(foreground.contains("android:strokeLineCap=\"round\""))
        assertTrue(foreground.contains("android:strokeLineJoin=\"round\""))
        assertEquals(3, foreground.countOccurrences("android:fillColor=\"@android:color/transparent\""))
        assertEquals(3, foreground.countOccurrences("android:strokeColor=\"#FFE6E6E6\""))
        assertEquals(3, foreground.countOccurrences("android:strokeWidth=\"2\""))
        assertEquals(
            listOf(
                "M10,13a2,201,04,0a2,201,0-4,0",
                "M13.45,11.55l2.05,-2.05",
                "M6.4,20a9,901,111.2,0l-11.2,0",
            ),
            pathData(foreground).map { it.replace(Regex("\\s+"), "") },
        )

        assertFalse(foreground.contains("<bitmap"))
        assertFalse(foreground.contains("<inset"))
        assertFalse(foreground.contains("android:translateX"))
        assertFalse(foreground.contains("android:translateY"))
        assertFalse(foreground.contains("M38.8,75.75"))
        assertFalse(foreground.contains("M54,48"))
        assertFalse(foreground.contains("M57.2,56.1"))
        assertFalse(compact.contains("M19.5,120"))
    }

    @Test
    fun splashIsIndependentButUsesTheSameDashboardSvgGeometry() {
        val styles = resourceText("values/styles.xml")
        val foreground = resourceText("drawable/ic_launcher_foreground.xml")
        val splash = resourceText("drawable/ic_launcher_splash.xml")

        assertTrue(styles.contains("@drawable/ic_launcher_splash"))
        assertTrue(splash.contains("<vector"))
        assertTrue(splash.contains("android:viewportWidth=\"24\""))
        assertTrue(splash.contains("android:viewportHeight=\"24\""))
        assertFalse(splash.contains("<bitmap"))
        assertFalse(splash.contains("<inset"))
        assertEquals(vectorBody(foreground), vectorBody(splash))
    }

    @Test
    fun manifestKeepsAdaptiveLauncherReferences() {
        val manifest = mainSourceFile("AndroidManifest.xml").readText()

        assertTrue(manifest.contains("android:icon=\"@mipmap/ic_launcher\""))
        assertTrue(manifest.contains("android:roundIcon=\"@mipmap/ic_launcher_round\""))
    }

    private fun pathData(source: String): List<String> =
        Regex("""android:pathData=\"([^\"]+)\"""")
            .findAll(source)
            .map { it.groupValues[1] }
            .toList()

    private fun String.countOccurrences(value: String): Int =
        windowed(value.length, 1).count { it == value }

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
