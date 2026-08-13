package com.codexquotatray.android.update

import java.net.URI

internal enum class ReleaseNotesBlockKind {
    HEADING,
    PARAGRAPH,
    UNORDERED_LIST_ITEM,
    ORDERED_LIST_ITEM,
    QUOTE,
    CODE_BLOCK,
}

internal enum class ReleaseNotesInlineKind {
    TEXT,
    BOLD,
    ITALIC,
    INLINE_CODE,
    LINK,
}

internal data class ReleaseNotesInline(
    val kind: ReleaseNotesInlineKind,
    val text: String,
    val url: String? = null,
)

internal data class ReleaseNotesBlock(
    val kind: ReleaseNotesBlockKind,
    val level: Int = 0,
    val inlines: List<ReleaseNotesInline>,
    val listIndex: Int = 0,
)

internal data class ReleaseNotesDocument(
    val blocks: List<ReleaseNotesBlock>,
    val wasTruncated: Boolean,
)

/**
 * A deliberately small, fail-soft Markdown parser for release notes.
 *
 * The block and inline model mirrors the Windows renderer. It is intentionally
 * not a general Markdown implementation: unsupported syntax remains text.
 */
internal object ReleaseNotesMarkdown {
    const val DEFAULT_MAX_CHARACTERS = 12_000

    fun parse(
        markdown: String?,
        maxCharacters: Int = DEFAULT_MAX_CHARACTERS,
    ): ReleaseNotesDocument {
        val source = markdown.orEmpty()
        if (source.isBlank()) {
            return ReleaseNotesDocument(emptyList(), wasTruncated = false)
        }

        val (limitedSource, wasTruncated) = truncateSource(source, maxCharacters)
        return ReleaseNotesDocument(
            blocks = parseBlocks(limitedSource),
            wasTruncated = wasTruncated,
        )
    }

    private fun parseBlocks(markdown: String): List<ReleaseNotesBlock> {
        val lines = markdown
            .replace("\r\n", "\n")
            .replace('\r', '\n')
            .split('\n')
        val blocks = mutableListOf<ReleaseNotesBlock>()
        val paragraph = mutableListOf<String>()
        val code = mutableListOf<String>()
        var inCode = false

        fun flushParagraph() {
            if (paragraph.isEmpty()) return
            blocks += ReleaseNotesBlock(
                kind = ReleaseNotesBlockKind.PARAGRAPH,
                inlines = parseInline(paragraph.joinToString("\n")),
            )
            paragraph.clear()
        }

        fun flushCode() {
            blocks += ReleaseNotesBlock(
                kind = ReleaseNotesBlockKind.CODE_BLOCK,
                inlines = listOf(
                    ReleaseNotesInline(
                        kind = ReleaseNotesInlineKind.TEXT,
                        text = code.joinToString("\n"),
                    ),
                ),
            )
            code.clear()
        }

        for (rawLine in lines) {
            val line = rawLine.trimEnd(' ', '\t')
            val trimmed = line.trimStart()

            if (inCode) {
                if (trimmed.startsWith("\u0060\u0060\u0060")) {
                    flushCode()
                    inCode = false
                } else {
                    code += line
                }
                continue
            }

            if (trimmed.startsWith("\u0060\u0060\u0060")) {
                flushParagraph()
                inCode = true
                continue
            }

            if (line.isBlank()) {
                flushParagraph()
                continue
            }

            parseHeading(trimmed)?.let { (level, text) ->
                flushParagraph()
                blocks += ReleaseNotesBlock(
                    kind = ReleaseNotesBlockKind.HEADING,
                    level = level,
                    inlines = parseInline(text),
                )
                continue
            }

            parseUnorderedItem(trimmed)?.let { text ->
                flushParagraph()
                blocks += ReleaseNotesBlock(
                    kind = ReleaseNotesBlockKind.UNORDERED_LIST_ITEM,
                    inlines = parseInline(text),
                )
                continue
            }

            parseOrderedItem(trimmed)?.let { (index, text) ->
                flushParagraph()
                blocks += ReleaseNotesBlock(
                    kind = ReleaseNotesBlockKind.ORDERED_LIST_ITEM,
                    inlines = parseInline(text),
                    listIndex = index,
                )
                continue
            }

            if (trimmed.startsWith(">")) {
                flushParagraph()
                blocks += ReleaseNotesBlock(
                    kind = ReleaseNotesBlockKind.QUOTE,
                    inlines = parseInline(trimmed.removePrefix(">").trimStart()),
                )
                continue
            }

            paragraph += line
        }

        if (inCode) {
            flushCode()
        }
        flushParagraph()
        return blocks
    }

    internal fun parseInline(text: String): List<ReleaseNotesInline> {
        val result = mutableListOf<ReleaseNotesInline>()
        var plainStart = 0
        var index = 0

        fun flushPlain(end: Int) {
            if (end > plainStart) {
                result += ReleaseNotesInline(
                    kind = ReleaseNotesInlineKind.TEXT,
                    text = text.substring(plainStart, end),
                )
            }
        }

        while (index < text.length) {
            if (text.startsWith("**", index)) {
                val close = text.indexOf("**", index + 2)
                if (close > index + 2) {
                    flushPlain(index)
                    result += ReleaseNotesInline(
                        kind = ReleaseNotesInlineKind.BOLD,
                        text = text.substring(index + 2, close),
                    )
                    index = close + 2
                    plainStart = index
                    continue
                }
            } else if (text[index] == '*') {
                val close = text.indexOf('*', index + 1)
                if (close > index + 1) {
                    flushPlain(index)
                    result += ReleaseNotesInline(
                        kind = ReleaseNotesInlineKind.ITALIC,
                        text = text.substring(index + 1, close),
                    )
                    index = close + 1
                    plainStart = index
                    continue
                }
            } else if (text[index] == '\u0060') {
                val close = text.indexOf('\u0060', index + 1)
                if (close > index + 1) {
                    flushPlain(index)
                    result += ReleaseNotesInline(
                        kind = ReleaseNotesInlineKind.INLINE_CODE,
                        text = text.substring(index + 1, close),
                    )
                    index = close + 1
                    plainStart = index
                    continue
                }
            } else if (text[index] == '[') {
                val link = tryParseSafeLink(text, index)
                if (link != null) {
                    flushPlain(index)
                    result += ReleaseNotesInline(
                        kind = ReleaseNotesInlineKind.LINK,
                        text = link.label,
                        url = link.url,
                    )
                    index = link.endExclusive
                    plainStart = index
                    continue
                }
            }

            index++
        }

        flushPlain(text.length)
        return result
    }

    internal fun isSafeHttpsUrl(value: String): Boolean = runCatching {
        val uri = URI(value)
        uri.scheme.equals("https", ignoreCase = true) && !uri.host.isNullOrBlank()
    }.getOrDefault(false)

    private fun parseHeading(line: String): Pair<Int, String>? {
        var level = 0
        while (level < 3 && level < line.length && line[level] == '#') {
            level++
        }
        if (level == 0 || level >= line.length || !line[level].isWhitespace()) {
            return null
        }

        val text = line.substring(level + 1).trim()
        return text.takeIf { it.isNotEmpty() }?.let { level to it }
    }

    private fun parseUnorderedItem(line: String): String? {
        if (line.length < 3 || line[0] !in charArrayOf('-', '*', '+') || !line[1].isWhitespace()) {
            return null
        }
        return line.substring(2).trim().takeIf { it.isNotEmpty() }
    }

    private fun parseOrderedItem(line: String): Pair<Int, String>? {
        val separator = line.indexOf('.')
        if (separator <= 0 || separator + 1 >= line.length || !line[separator + 1].isWhitespace()) {
            return null
        }
        val index = line.substring(0, separator).toIntOrNull()?.takeIf { it > 0 } ?: return null
        val text = line.substring(separator + 2).trim().takeIf { it.isNotEmpty() } ?: return null
        return index to text
    }

    private data class LinkMatch(
        val label: String,
        val url: String,
        val endExclusive: Int,
    )

    private fun tryParseSafeLink(text: String, start: Int): LinkMatch? {
        val closeLabel = text.indexOf(']', start + 1)
        if (closeLabel <= start + 1 || closeLabel + 2 >= text.length || text[closeLabel + 1] != '(') {
            return null
        }
        val closeUrl = text.indexOf(')', closeLabel + 2)
        if (closeUrl <= closeLabel + 2) {
            return null
        }

        val url = text.substring(closeLabel + 2, closeUrl)
        if (!isSafeHttpsUrl(url)) return null
        return LinkMatch(
            label = text.substring(start + 1, closeLabel),
            url = url,
            endExclusive = closeUrl + 1,
        )
    }

    private fun truncateSource(source: String, maxCharacters: Int): Pair<String, Boolean> {
        val limit = maxCharacters.coerceAtLeast(0)
        if (source.length <= limit) return source to false

        var end = limit
        if (end > 0 && end < source.length
            && Character.isHighSurrogate(source[end - 1])
            && Character.isLowSurrogate(source[end])) {
            end--
        }
        return source.substring(0, end) to true
    }
}
