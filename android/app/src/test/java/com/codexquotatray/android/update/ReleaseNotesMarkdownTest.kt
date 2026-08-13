package com.codexquotatray.android.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReleaseNotesMarkdownTest {
    @Test
    fun parsesPlainParagraph() {
        val document = ReleaseNotesMarkdown.parse("第一行\n第二行")

        assertEquals(1, document.blocks.size)
        assertEquals(ReleaseNotesBlockKind.PARAGRAPH, document.blocks.single().kind)
        assertEquals("第一行\n第二行", document.blocks.single().inlines.single().text)
    }

    @Test
    fun parsesHeadingLevels() {
        val blocks = ReleaseNotesMarkdown.parse("# 一级\n## 二级\n### 三级").blocks

        assertEquals(listOf(1, 2, 3), blocks.map { it.level })
        assertTrue(blocks.all { it.kind == ReleaseNotesBlockKind.HEADING })
    }

    @Test
    fun parsesUnorderedListMarkers() {
        val blocks = ReleaseNotesMarkdown.parse("- one\n* two\n+ three").blocks

        assertEquals(3, blocks.size)
        assertTrue(blocks.all { it.kind == ReleaseNotesBlockKind.UNORDERED_LIST_ITEM })
        assertEquals(listOf("one", "two", "three"), blocks.map { it.inlines.single().text })
    }

    @Test
    fun parsesOrderedListAndKeepsOriginalNumbers() {
        val blocks = ReleaseNotesMarkdown.parse("1. first\n3. third").blocks

        assertEquals(listOf(1, 3), blocks.map { it.listIndex })
        assertTrue(blocks.all { it.kind == ReleaseNotesBlockKind.ORDERED_LIST_ITEM })
    }

    @Test
    fun parsesQuote() {
        val block = ReleaseNotesMarkdown.parse("> 感谢使用").blocks.single()

        assertEquals(ReleaseNotesBlockKind.QUOTE, block.kind)
        assertEquals("感谢使用", block.inlines.single().text)
    }

    @Test
    fun parsesFencedCodeBlockIncludingUnclosedFence() {
        val closed = ReleaseNotesMarkdown.parse("\u0060\u0060\u0060\nval x = 1\n\u0060\u0060\u0060").blocks.single()
        val unclosed = ReleaseNotesMarkdown.parse("\u0060\u0060\u0060\nval y = 2").blocks.single()

        assertEquals(ReleaseNotesBlockKind.CODE_BLOCK, closed.kind)
        assertEquals("val x = 1", closed.inlines.single().text)
        assertEquals(ReleaseNotesBlockKind.CODE_BLOCK, unclosed.kind)
        assertEquals("val y = 2", unclosed.inlines.single().text)
    }

    @Test
    fun parsesBoldItalicAndInlineCode() {
        val inlines = ReleaseNotesMarkdown.parseInline(
            "支持 **加粗**、*倾斜* 和 \u0060Token\u0060",
        )

        assertEquals(
            listOf(
                ReleaseNotesInlineKind.TEXT,
                ReleaseNotesInlineKind.BOLD,
                ReleaseNotesInlineKind.TEXT,
                ReleaseNotesInlineKind.ITALIC,
                ReleaseNotesInlineKind.TEXT,
                ReleaseNotesInlineKind.INLINE_CODE,
            ),
            inlines.map { it.kind },
        )
        assertEquals("加粗", inlines[1].text)
        assertEquals("倾斜", inlines[3].text)
        assertEquals("Token", inlines[5].text)
    }

    @Test
    fun onlyHttpsLinksBecomeLinkInlines() {
        val inlines = ReleaseNotesMarkdown.parseInline(
            "[安全](https://example.com) [不安全](http://example.com) [脚本](javascript:alert(1))",
        )

        assertEquals(1, inlines.count { it.kind == ReleaseNotesInlineKind.LINK })
        val link = inlines.first { it.kind == ReleaseNotesInlineKind.LINK }
        assertEquals("安全", link.text)
        assertEquals("https://example.com", link.url)
        assertTrue(
            inlines.filter { it.kind == ReleaseNotesInlineKind.TEXT }
                .joinToString("") { it.text }
                .contains("[不安全](http://example.com)"),
        )
        assertFalse(inlines.any { it.url?.startsWith("javascript:") == true })
    }

    @Test
    fun preservesChineseEmojiAndMalformedMarkersWithoutThrowing() {
        val document = runCatching {
            ReleaseNotesMarkdown.parse("中文 😀 **未闭合\n下一行 \u0060仍未闭合")
        }.getOrThrow()
        val text = document.blocks.flatMap { it.inlines }.joinToString("") { it.text }

        assertTrue(text.contains("中文 😀"))
        assertTrue(text.contains("**未闭合"))
        assertTrue(text.contains("\u0060仍未闭合"))
    }

    @Test
    fun truncatesAtTwelveThousandCharactersAndReportsIt() {
        val document = ReleaseNotesMarkdown.parse("x".repeat(12_001))

        assertTrue(document.wasTruncated)
        assertTrue(document.blocks.flatMap { it.inlines }.sumOf { it.text.length } <= 12_000)
        assertFalse(ReleaseNotesMarkdown.parse("x".repeat(12_000)).wasTruncated)
    }

    @Test
    fun parsesCompositeReleaseNotesFixture() {
        val document = ReleaseNotesMarkdown.parse(
            listOf(
                "## v0.x.x",
                "",
                "### 新功能",
                "",
                "- 支持 **Markdown**",
                "- 优化 " + "\u0060" + "Token" + "\u0060 后台同步",
                "- 修复 [更新检查](https://github.com/DDJang/CodexQuotaTray)",
                "",
                "> 感谢使用 CodexQuotaTray。",
            ).joinToString("\n"),
        )

        assertEquals(6, document.blocks.size)
        assertEquals(ReleaseNotesBlockKind.HEADING, document.blocks[0].kind)
        assertEquals(ReleaseNotesBlockKind.HEADING, document.blocks[1].kind)
        assertEquals(3, document.blocks.count { it.kind == ReleaseNotesBlockKind.UNORDERED_LIST_ITEM })
        assertEquals(ReleaseNotesBlockKind.QUOTE, document.blocks.last().kind)
    }

    @Test
    fun emptyNotesProduceNoBlocks() {
        val document = ReleaseNotesMarkdown.parse("  \n\n")

        assertTrue(document.blocks.isEmpty())
        assertFalse(document.wasTruncated)
    }
}
