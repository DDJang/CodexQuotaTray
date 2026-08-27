package com.codexquotatray.android

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.InflaterInputStream

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
    fun foregroundAndMonochromeAvoidNestedInsets() {
        val foreground = resourceText("drawable/ic_launcher_foreground.xml")
        val monochrome = resourceText("drawable/ic_launcher_monochrome.xml")

        listOf(foreground, monochrome).forEach { source ->
            assertTrue(source.contains("@drawable/ic_launcher_foreground_mark"))
            assertTrue(!source.contains("<inset"))
            assertTrue(!source.contains("18dp"))
        }
        assertTrue(monochrome.contains("android:tint=\"@android:color/white\""))
    }

    @Test
    fun manifestKeepsAdaptiveLauncherReferences() {
        val manifest = mainSourceFile("AndroidManifest.xml").readText()

        assertTrue(manifest.contains("android:icon=\"@mipmap/ic_launcher\""))
        assertTrue(manifest.contains("android:roundIcon=\"@mipmap/ic_launcher_round\""))
    }

    @Test
    fun normalizedForegroundPngHasSafeNonEmptyAlphaBounds() {
        val imageFile = resourceFile("drawable-nodpi/ic_launcher_foreground_mark.png")
        val image = readRgbaPng(imageFile)

        val opaquePixels = image.alpha.withIndex().filter { it.value != 0 }
        assertTrue(opaquePixels.isNotEmpty())
        val xs = opaquePixels.map { it.index % image.width }
        val ys = opaquePixels.map { it.index / image.width }
        val widthRatio = (xs.max() - xs.min() + 1).toDouble() / image.width
        val heightRatio = (ys.max() - ys.min() + 1).toDouble() / image.height
        assertTrue(widthRatio in 0.40..0.65)
        assertTrue(heightRatio in 0.40..0.65)
    }

    private data class RgbaPng(val width: Int, val height: Int, val alpha: IntArray)

    private fun readRgbaPng(file: File): RgbaPng {
        val bytes = file.readBytes()
        require(bytes.take(8).toByteArray().contentEquals(PNG_SIGNATURE)) { "Invalid PNG signature" }
        val compressed = ByteArrayOutputStream()
        var width = 0
        var height = 0
        var offset = 8
        while (offset < bytes.size) {
            val length = readInt(bytes, offset)
            val type = bytes.copyOfRange(offset + 4, offset + 8).decodeToString()
            val dataStart = offset + 8
            when (type) {
                "IHDR" -> {
                    width = readInt(bytes, dataStart)
                    height = readInt(bytes, dataStart + 4)
                    require(bytes[dataStart + 8].toInt() == 8) { "Only 8-bit PNG is supported" }
                    require(bytes[dataStart + 9].toInt() == 6) { "Foreground PNG must be RGBA" }
                }
                "IDAT" -> compressed.write(bytes, dataStart, length)
                "IEND" -> break
            }
            offset = dataStart + length + 4
        }
        require(width > 0 && height > 0 && compressed.size() > 0) { "Incomplete PNG" }

        val inflated = InflaterInputStream(ByteArrayInputStream(compressed.toByteArray())).readBytes()
        val bytesPerPixel = 4
        val stride = width * bytesPerPixel
        require(inflated.size == height * (stride + 1)) { "Unexpected PNG scanline size" }
        val alpha = IntArray(width * height)
        var previous = IntArray(stride)
        var inputOffset = 0
        repeat(height) { y ->
            val filter = inflated[inputOffset++].toInt() and 0xff
            val current = IntArray(stride)
            for (index in 0 until stride) {
                val encoded = inflated[inputOffset++].toInt() and 0xff
                val left = if (index >= bytesPerPixel) current[index - bytesPerPixel] else 0
                val up = previous[index]
                val upperLeft = if (index >= bytesPerPixel) previous[index - bytesPerPixel] else 0
                val predictor = when (filter) {
                    0 -> 0
                    1 -> left
                    2 -> up
                    3 -> (left + up) / 2
                    4 -> paeth(left, up, upperLeft)
                    else -> error("Unsupported PNG filter $filter")
                }
                current[index] = (encoded + predictor) and 0xff
            }
            for (x in 0 until width) {
                alpha[y * width + x] = current[x * bytesPerPixel + 3]
            }
            previous = current
        }
        return RgbaPng(width, height, alpha)
    }

    private fun readInt(bytes: ByteArray, offset: Int): Int =
        ByteBuffer.wrap(bytes, offset, 4).order(ByteOrder.BIG_ENDIAN).int

    private fun paeth(left: Int, up: Int, upperLeft: Int): Int {
        val estimate = left + up - upperLeft
        val leftDistance = kotlin.math.abs(estimate - left)
        val upDistance = kotlin.math.abs(estimate - up)
        val upperLeftDistance = kotlin.math.abs(estimate - upperLeft)
        return when {
            leftDistance <= upDistance && leftDistance <= upperLeftDistance -> left
            upDistance <= upperLeftDistance -> up
            else -> upperLeft
        }
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

    private companion object {
        val PNG_SIGNATURE = byteArrayOf(137.toByte(), 80, 78, 71, 13, 10, 26, 10)
    }
}
