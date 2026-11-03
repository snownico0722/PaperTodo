package io.github.papertodo.mobile.data

import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener
import java.io.IOException
import java.util.UUID

data class TodoItem(
    val id: String,
    val text: String,
    val done: Boolean,
    val order: Int,
    val linkedNoteId: String? = null,
    val linkedPath: String? = null
)

data class Paper(
    val id: String,
    val type: String,
    val title: String,
    val content: String,
    val items: List<TodoItem>
)

/** The original JSON tree is the document. Projections must never replace unknown fields. */
internal class PaperDocument(val json: JSONObject) {
    val paperArray: JSONArray get() = json.getJSONArray("papers")

    fun snapshots(): List<Paper> = (0 until paperArray.length()).map { index ->
        val p = paperArray.getJSONObject(index)
        val items = p.optJSONArray("items") ?: JSONArray()
        Paper(
            p.getString("id"), p.optString("type", "todo"), p.optString("title", ""),
            p.optString("content", ""),
            (0 until items.length()).map { itemIndex ->
                val item = items.getJSONObject(itemIndex)
                TodoItem(item.getString("id"), item.optString("text", ""),
                    item.optBoolean("done", false), item.optInt("order", itemIndex),
                    item.nullableString("linkedNoteId"), item.nullableString("linkedPath"))
            }.sortedBy { it.order }
        )
    }

    fun paper(id: String): JSONObject? = (0 until paperArray.length())
        .map { paperArray.getJSONObject(it) }.firstOrNull { it.optString("id") == id }

    fun replacePaper(id: String, replacement: JSONObject) {
        for (index in 0 until paperArray.length()) {
            if (paperArray.getJSONObject(index).getString("id") == id) {
                paperArray.put(index, replacement)
                return
            }
        }
    }

    fun referencedImageIds(): Set<String> = snapshots().asSequence()
        .filter { it.type == "note" }.flatMap { ImageReferences.ids(it.content).asSequence() }.toSet()

    companion object {
        const val MAX_JSON_BYTES = 16 * 1024 * 1024

        fun parse(text: String): PaperDocument {
            try {
                if (text.toByteArray(Charsets.UTF_8).size > MAX_JSON_BYTES) throw IOException("数据文件超过 16 MiB")
                val tokener = JSONTokener(text.removePrefix("\uFEFF"))
                val root = tokener.nextValue() as? JSONObject ?: throw IOException("数据必须是 JSON 对象")
                if (tokener.nextClean() != '\u0000') throw IOException("JSON 后存在多余内容")
                val papers = root.optJSONArray("papers") ?: throw IOException("缺少 papers 数组")
                val ids = mutableSetOf<String>()
                for (index in 0 until papers.length()) {
                    val paper = papers.optJSONObject(index) ?: throw IOException("纸片格式错误")
                    val id = paper.opt("id") as? String ?: throw IOException("纸片缺少 ID")
                    if (id.isBlank() || !ids.add(id)) throw IOException("纸片 ID 为空或重复")
                    val type = paper.optString("type", "todo")
                    if (type != "todo" && type != "note") throw IOException("仅支持 v3.31 的待办和笔记纸片")
                    checkString(paper, "title")
                    checkString(paper, "content")
                    val items = if (!paper.has("items")) JSONArray() else paper.optJSONArray("items")
                        ?: throw IOException("待办列表格式错误")
                    val itemIds = mutableSetOf<String>()
                    for (itemIndex in 0 until items.length()) {
                        val item = items.optJSONObject(itemIndex) ?: throw IOException("待办格式错误")
                        val itemId = item.opt("id") as? String ?: throw IOException("待办缺少 ID")
                        if (itemId.isBlank() || !itemIds.add(itemId)) throw IOException("待办 ID 为空或重复")
                        checkString(item, "text")
                        checkString(item, "linkedNoteId", true)
                        checkString(item, "linkedPath", true)
                        if (item.has("done") && item.opt("done") !is Boolean) throw IOException("待办完成状态格式错误")
                        if (item.has("order")) {
                            val order = item.opt("order") as? Number ?: throw IOException("待办排序格式错误")
                            if (!order.toDouble().isFinite() || order.toDouble() != order.toInt().toDouble()) throw IOException("待办排序应为整数")
                        }
                    }
                }
                return PaperDocument(root)
            } catch (error: IOException) {
                throw error
            } catch (error: Exception) {
                throw IOException("无法读取 PaperTodo 数据：${error.message}", error)
            }
        }

        private fun checkString(json: JSONObject, key: String, nullable: Boolean = false) {
            if (!json.has(key)) return
            val value = json.opt(key)
            if (nullable && value == JSONObject.NULL) return
            if (value !is String) throw IOException("字段 $key 应为文字")
        }

        fun empty(): PaperDocument = PaperDocument(JSONObject()
            .put("papers", JSONArray()).put("uiLanguage", "zh-CN")
            .put("theme", "system").put("colorScheme", "warm")
            .put("markdownRenderMode", "enhanced").put("imageReferenceTextMode", "always")
            .put("todoVisualSize", "medium").put("autoCompressLargeImages", true)
            .put("enableTodoNoteLinks", true).put("enableAnimations", true)
            .put("useCapsuleMode", true).put("useDeepCapsuleMode", true)
            .put("zoom", 1.0))

        fun newPaper(type: String, title: String): JSONObject {
            require(type == "todo" || type == "note")
            return JSONObject().put("id", newId()).put("type", type).put("title", title)
                .put("x", 120).put("y", 120).put("width", if (type == "note") 320 else 280)
                .put("height", if (type == "note") 360 else 340).put("isVisible", true)
                .put("alwaysOnTop", false).put("isCollapsed", false).put("textZoom", 1.0)
                .put("items", JSONArray()).put("content", "")
        }

        fun newId(): String = UUID.randomUUID().toString().replace("-", "")
    }
}

internal fun JSONObject.nullableString(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key).takeIf { it.isNotBlank() }

/** Only real stand-alone i: image blocks count, never examples inside fenced code. */
object ImageReferences {
    private val fence = Regex("^ {0,3}(`{3,}|~{3,})(.*)$")

    fun idForLine(line: String): String? {
        val text = line.trim()
        if (!text.startsWith("![")) return null
        val labelEnd = text.indexOf("](", 2)
        if (labelEnd < 0) return null
        val end = text.indexOf(')', labelEnd + 2)
        if (end <= labelEnd + 2) return null
        val tail = text.substring(end + 1).trim()
        if (tail.isNotEmpty() && !(tail.startsWith("{") && tail.endsWith("}"))) return null
        val url = text.substring(labelEnd + 2, end).trim()
        if (!url.startsWith("i:")) return null
        return url.substring(2).trim().takeIf { ImageMetadata.ID.matches(it) }
    }

    fun ids(content: String): Set<String> {
        val result = linkedSetOf<String>()
        var fenceCharacter: Char? = null
        var fenceLength = 0
        for (line in content.lineSequence()) {
            val match = fence.matchEntire(line)
            if (fenceCharacter != null) {
                if (match != null && match.groupValues[1][0] == fenceCharacter &&
                    match.groupValues[1].length >= fenceLength && match.groupValues[2].isBlank()) {
                    fenceCharacter = null
                }
                continue
            }
            if (match != null) {
                fenceCharacter = match.groupValues[1][0]
                fenceLength = match.groupValues[1].length
                continue
            }
            idForLine(line)?.let { result.add(it) }
        }
        return result
    }
}

object TodoTextSplitter {
    private val checkbox = Regex("^\\s*(?:[-*+]\\s+)?\\[[ xX]\\]\\s*")
    private val bullet = Regex("^\\s*(?:[-*+•·▪◦]|[0-9]+[.)、])\\s+")
    fun split(text: String): List<String> = text.replace("\r\n", "\n").replace('\r', '\n')
        .split('\n').map { bullet.replaceFirst(checkbox.replaceFirst(it.trim(), ""), "").trim() }
        .filter { it.isNotEmpty() }
}
