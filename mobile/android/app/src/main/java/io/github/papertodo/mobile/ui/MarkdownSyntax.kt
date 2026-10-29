package io.github.papertodo.mobile.ui

import org.commonmark.ext.gfm.strikethrough.Strikethrough
import org.commonmark.ext.gfm.strikethrough.StrikethroughExtension
import org.commonmark.node.*
import org.commonmark.parser.IncludeSourceSpans
import org.commonmark.parser.Parser

internal enum class MarkdownKind {
    HEADING, BOLD, ITALIC, STRIKE, UNDERLINE, CODE, CODE_BLOCK, QUOTE, SYNTAX, LINK, BULLET, RULE,
}

internal data class MarkdownDecoration(
    val kind: MarkdownKind,
    val start: Int,
    val end: Int,
    val value: Int = 0,
    val target: String = "",
)

internal data class MarkdownImageReference(
    val id: String,
    val start: Int,
    val end: Int,
    val label: String,
    val width: Double? = null,
    val percent: Double? = null,
    val attribute: Boolean = false,
)

internal data class MarkdownPlan(
    val source: String,
    val decorations: List<MarkdownDecoration>,
    val images: List<MarkdownImageReference>,
)

/** Parser-backed source decorations: offsets always refer to the unchanged Markdown. */
internal object MarkdownSyntax {
    private val parser = Parser.builder()
        .extensions(listOf(StrikethroughExtension.create()))
        .includeSourceSpans(IncludeSourceSpans.BLOCKS_AND_INLINES)
        .build()

    private val imageLine = Regex("^\\s*!\\[(.*?)\\]\\(\\s*i:([0-9]{3,8})\\s*\\)\\s*(\\{[^}]*\\})?\\s*$")
    private val dimensions = Regex("^([0-9]+(?:\\.[0-9]+)?)\\s*[x×]\\s*([0-9]+(?:\\.[0-9]+)?)$")
    private val attributeWidth = Regex("(?:^|[\\s,;])width\\s*[=:]\\s*([0-9]+(?:\\.[0-9]+)?)(%|px)?(?=$|[\\s,;])", RegexOption.IGNORE_CASE)
    private val htmlTag = Regex("^<(/?)(b|strong|i|em|s|del|u|code|a)(\\s[^>]*)?>$", RegexOption.IGNORE_CASE)
    private val href = Regex("\\bhref\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOption.IGNORE_CASE)

    fun compile(source: String): MarkdownPlan {
        if (source.isEmpty()) return MarkdownPlan(source, emptyList(), emptyList())
        val offsets = lineOffsets(source)
        val document = parser.parse(source)
        val decorations = mutableListOf<MarkdownDecoration>()
        val codeRanges = mutableListOf<IntRange>()
        val html = mutableListOf<Pair<IntRange, String>>()

        fun range(node: Node): IntRange? {
            val spans = node.sourceSpans
            if (spans.isEmpty()) return null
            val start = spans.minOf { (offsets.getOrNull(it.lineIndex) ?: source.length) + it.columnIndex }
            val end = spans.maxOf { (offsets.getOrNull(it.lineIndex) ?: source.length) + it.columnIndex + it.length }
            return if (start in 0 until source.length && end > start) start until end.coerceAtMost(source.length) else null
        }

        fun add(kind: MarkdownKind, start: Int, end: Int, value: Int = 0, target: String = "") {
            if (start >= 0 && end <= source.length && start < end) {
                decorations += MarkdownDecoration(kind, start, end, value, target)
            }
        }

        fun syntax(start: Int, end: Int) = add(MarkdownKind.SYNTAX, start, end)
        fun markers(r: IntRange, count: Int) {
            if (r.last - r.first + 1 >= count * 2) {
                syntax(r.first, r.first + count)
                syntax(r.last + 1 - count, r.last + 1)
            }
        }

        fun visit(node: Node) {
            val r = range(node)
            if (r != null) {
                val start = r.first
                val end = r.last + 1
                val raw = source.substring(start, end)
                when (node) {
                    is Heading -> {
                        add(MarkdownKind.HEADING, start, end, node.level)
                        Regex("^ {0,3}#{1,6}(?:[ \\t]+|$)").find(raw)?.let { syntax(start, start + it.value.length) }
                        Regex("[ \\t]+#+[ \\t]*$").find(raw)?.let { syntax(start + it.range.first, end) }
                    }
                    is StrongEmphasis -> { add(MarkdownKind.BOLD, start, end); markers(r, 2) }
                    is Emphasis -> { add(MarkdownKind.ITALIC, start, end); markers(r, 1) }
                    is Strikethrough -> { add(MarkdownKind.STRIKE, start, end); markers(r, 2) }
                    is Code -> {
                        add(MarkdownKind.CODE, start, end)
                        markers(r, raw.takeWhile { it == '`' }.length.coerceAtLeast(1))
                        codeRanges += r
                    }
                    is FencedCodeBlock, is IndentedCodeBlock -> {
                        add(MarkdownKind.CODE_BLOCK, start, end)
                        codeRanges += r
                        if (node is FencedCodeBlock) {
                            Regex("(?m)^ {0,3}(?:`{3,}|~{3,})[^\\r\\n]*").findAll(raw).forEach {
                                syntax(start + it.range.first, start + it.range.last + 1)
                            }
                        }
                    }
                    is BlockQuote -> {
                        add(MarkdownKind.QUOTE, start, end)
                        Regex("(?m)^ {0,3}>[ \\t]?").findAll(raw).forEach {
                            syntax(start + it.range.first, start + it.range.last + 1)
                        }
                    }
                    is ListItem -> {
                        val marker = Regex("^ {0,3}([-+*]|[0-9]+[.)])[ \\t]+").find(raw)
                        if (marker != null) {
                            val token = marker.groups[1]!!
                            if (node.parent is BulletList) add(MarkdownKind.BULLET, start + token.range.first, start + token.range.last + 1)
                            else syntax(start + token.range.first, start + token.range.last + 1)
                        }
                    }
                    is ThematicBreak -> add(MarkdownKind.RULE, start, end)
                    is Link -> {
                        val childRanges = generateSequence(node.firstChild) { it.next }.mapNotNull { range(it) }.toList()
                        val labelStart = childRanges.minOfOrNull { it.first } ?: start
                        val labelEnd = childRanges.maxOfOrNull { it.last + 1 } ?: end
                        add(MarkdownKind.LINK, labelStart, labelEnd, target = node.destination)
                        if (labelStart > start) syntax(start, labelStart)
                        if (labelEnd < end) syntax(labelEnd, end)
                    }
                    is HtmlInline -> html += r to raw
                }
            }
            var child = node.firstChild
            while (child != null) {
                visit(child)
                child = child.next
            }
        }
        visit(document)

        data class OpenTag(val name: String, val range: IntRange, val url: String)
        val openTags = mutableListOf<OpenTag>()
        for ((r, raw) in html.sortedBy { it.first.first }) {
            val tag = htmlTag.matchEntire(raw) ?: continue
            val closing = tag.groupValues[1] == "/"
            val name = tag.groupValues[2].lowercase()
            if (!closing) {
                val urlMatch = href.find(tag.groupValues[3])
                val url = urlMatch?.groupValues?.drop(1)?.firstOrNull { it.isNotEmpty() }.orEmpty()
                openTags += OpenTag(name, r, url)
            } else {
                val index = openTags.indexOfLast { it.name == name }
                if (index < 0) continue
                val opened = openTags.removeAt(index)
                val contentStart = opened.range.last + 1
                val contentEnd = r.first
                when (name) {
                    "b", "strong" -> add(MarkdownKind.BOLD, contentStart, contentEnd)
                    "i", "em" -> add(MarkdownKind.ITALIC, contentStart, contentEnd)
                    "s", "del" -> add(MarkdownKind.STRIKE, contentStart, contentEnd)
                    "u" -> add(MarkdownKind.UNDERLINE, contentStart, contentEnd)
                    "code" -> add(MarkdownKind.CODE, contentStart, contentEnd)
                    "a" -> if (opened.url.isNotBlank()) add(MarkdownKind.LINK, contentStart, contentEnd, target = opened.url)
                }
                syntax(opened.range.first, opened.range.last + 1)
                syntax(r.first, r.last + 1)
            }
        }

        val images = offsets.mapNotNull { start ->
            val newline = source.indexOfAny(charArrayOf('\r', '\n'), start).let { if (it < 0) source.length else it }
            if (start == newline || codeRanges.any { start in it }) return@mapNotNull null
            val match = imageLine.matchEntire(source.substring(start, newline)) ?: return@mapNotNull null
            val label = match.groupValues[1]
            var width: Double? = null
            var percent: Double? = null
            label.substringAfter('|', "").split(',').map(String::trim).forEach { spec ->
                val size = dimensions.matchEntire(spec)
                if (size != null) width = positive(size.groupValues[1])
                if (spec.endsWith('%')) percent = positive(spec.dropLast(1))
            }
            val attr = attributeWidth.find(match.groupValues[3].removeSurrounding("{", "}"))
            if (attr != null) {
                val value = positive(attr.groupValues[1])
                if (attr.groupValues[2] == "%") { percent = value; width = null }
                else { width = value; percent = null }
            }
            MarkdownImageReference(match.groupValues[2], start, newline, label, width, percent, attr != null)
        }
        return MarkdownPlan(source, decorations, images)
    }

    private fun positive(value: String): Double? = value.toDoubleOrNull()?.takeIf { it.isFinite() && it > 0 }

    private fun lineOffsets(source: String): List<Int> {
        val result = mutableListOf(0)
        var index = 0
        while (index < source.length) {
            if (source[index] == '\r') {
                if (index + 1 < source.length && source[index + 1] == '\n') index++
                result += index + 1
            } else if (source[index] == '\n') result += index + 1
            index++
        }
        return result
    }
}
