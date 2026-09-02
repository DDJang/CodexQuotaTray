package com.codexquotatray.android

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class WidgetFixtureStructureTest {
    @Test
    fun previewRenderingDoesNotAcquireDataOrBindProductionActions() {
        val renderer = sourceFile("widget/QuotaWidgetRenderer.kt")
        val productionUpdate = renderer
            .substringAfter("private fun update(context: Context, manager: AppWidgetManager, ids: IntArray) {")
            .substringBefore("internal fun createPreviewRemoteViews(")
        assertTrue(productionUpdate.contains("QuotaWidgetStore(context).load()"))
        assertTrue(productionUpdate.contains("bindOpenAction = true"))
        assertTrue(productionUpdate.contains("manager.updateAppWidget"))

        val previewFactory = renderer
            .substringAfter("internal fun createPreviewRemoteViews(")
            .substringBefore("private fun createRemoteViews(")
        assertTrue(previewFactory.contains("bindOpenAction = false"))
        assertFalse(previewFactory.contains("QuotaWidgetStore"))
        assertFalse(previewFactory.contains("AppWidgetManager"))
        assertFalse(previewFactory.contains("PendingIntent"))

        val sharedFactory = renderer
            .substringAfter("private fun createRemoteViews(")
            .substringBefore("private fun bindOpenAction(")
        assertTrue(sharedFactory.contains("R.layout.widget_quota"))
        assertTrue(sharedFactory.contains("renderProjection(context, views, projection)"))

        val presentation = renderer
            .substringAfter("internal fun renderProjection(")
            .substringBefore("internal fun formatWidgetTokenToday")
        assertFalse(presentation.contains("QuotaWidgetStore"))
        assertFalse(presentation.contains("AppWidgetManager"))
        assertFalse(presentation.contains("PendingIntent"))
        assertFalse(presentation.contains("setOnClickPendingIntent"))
        assertTrue(renderer.contains("quotaProgressArgb"))
        assertTrue(renderer.contains("TokenFormatter::format"))
    }

    @Test
    fun debugFixtureUsesTheProductionXmlAndKeepsAllScenariosLocal() {
        val settings = sourceFile("SettingsActivity.kt")
        val developerOptions = settings
            .substringAfter("SettingsSection(\"开发者选项\")")
            .substringBefore("private fun openDebugQuotaRingFixture")
        assertTrue(developerOptions.contains("Quota Widget Fixture"))
        assertTrue(developerOptions.contains("openDebugQuotaWidgetFixture"))
        assertTrue(settings.contains("DEBUG_QUOTA_WIDGET_FIXTURE_ACTIVITY"))

        val manifest = debugManifestSource()
        val manifestEntry = manifest
            .substringAfter(".debug.QuotaWidgetFixtureActivity")
            .substringBefore("</activity>")
        assertTrue(manifestEntry.contains("android:configChanges=\"uiMode\""))
        assertTrue(manifestEntry.contains("android:exported=\"false\""))
        assertTrue(manifestEntry.contains("android:screenOrientation=\"portrait\""))
        assertFalse(manifestEntry.contains("intent-filter"))

        val fixture = debugSourceFile("debug/QuotaWidgetFixtureActivity.kt")
        listOf(
            "class QuotaWidgetFixtureActivity",
            "SecondaryScreenScaffold(title = \"主屏小组件\"",
            "SettingsSection(\"Debug 场景\")",
            "SettingsSection(\"尺寸\")",
            "SettingsSection(\"正式 Widget 预览\")",
            "SettingsSegmentedSelector",
            "AndroidView(",
            "QuotaWidgetRenderer.createPreviewRemoteViews",
            "remoteViews.apply(container.context, container)",
            "FIXTURE_TIME_MILLIS",
            "WidgetPreviewSize(widthDp = 360, heightDp = 132, selectorLabel = \"360×132 推荐\")",
            "WidgetPreviewSize(widthDp = 300, heightDp = 110, selectorLabel = \"300×110 最小\")",
            "defaultWidgetPreviewSizeIndex",
            "SettingsGroup(allowLiquidOverflow = true)",
            "Provider 最小尺寸",
            "EMPTY",
            "SINGLE_QUOTA",
            "DUAL_QUOTA",
            "DUAL_NO_TOKEN",
            "PARTIAL_UNAVAILABLE",
            "LARGE_TOKEN",
            "THRESHOLD_COLORS",
            "thresholdOptions",
            "QuotaWidgetProjection(",
            "QuotaWidgetWindow(",
            "QuotaWidgetTokenSummary(",
        ).forEach { marker -> assertTrue(fixture.contains(marker)) }
        assertFalse(fixture.contains("System.currentTimeMillis()"))
        assertFalse(fixture.contains("QuotaWidgetStore"))
        assertFalse(fixture.contains("AppWidgetManager"))
        assertFalse(fixture.contains("QuotaWidgetBridge"))
        assertFalse(fixture.contains("TokenUsageCache"))
        assertFalse(fixture.contains("OAuthStore"))
        assertFalse(fixture.contains("PendingIntent"))
        assertFalse(fixture.contains("sendBroadcast"))
        assertFalse(fixture.contains("MainActivity"))
        assertFalse(fixture.contains("debug_widget_fixture.xml"))
    }

    private fun sourceFile(relative: String): String {
        val candidates = listOf(
            File("android/app/src/main/java/com/codexquotatray/android/$relative"),
            File("app/src/main/java/com/codexquotatray/android/$relative"),
            File("src/main/java/com/codexquotatray/android/$relative"),
        )
        return candidates.firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("$relative source not found from ${System.getProperty("user.dir")}")
    }

    private fun debugSourceFile(relative: String): String {
        val candidates = listOf(
            File("android/app/src/debug/java/com/codexquotatray/android/$relative"),
            File("app/src/debug/java/com/codexquotatray/android/$relative"),
            File("src/debug/java/com/codexquotatray/android/$relative"),
        )
        return candidates.firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("$relative debug source not found from ${System.getProperty("user.dir")}")
    }

    private fun debugManifestSource(): String {
        val candidates = listOf(
            File("android/app/src/debug/AndroidManifest.xml"),
            File("app/src/debug/AndroidManifest.xml"),
            File("src/debug/AndroidManifest.xml"),
        )
        return candidates.firstOrNull(File::isFile)?.readNormalizedText()
            ?: error("debug AndroidManifest.xml not found from ${System.getProperty("user.dir")}")
    }

    private fun File.readNormalizedText(): String =
        readText()
            .replace("\r\n", "\n")
            .replace('\r', '\n')
}
