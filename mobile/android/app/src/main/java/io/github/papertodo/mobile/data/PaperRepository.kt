package io.github.papertodo.mobile.data

import android.content.Context
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.util.AtomicFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.ArrayDeque
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/** One owner for Activity, floating windows and disk writes. Desktop geometry stays in JSON. */
class PaperRepository(context: Context) : AutoCloseable {
    private val applicationContext = context.applicationContext
    private val gate = Any()
    private val ioGate = Any()
    private val main = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArraySet<() -> Unit>()
    private val writer = ScheduledThreadPoolExecutor(1) { runnable ->
        Thread(runnable, "PaperTodo-save").apply { isDaemon = true }
    }.apply { removeOnCancelPolicy = true }
    private val root = File(applicationContext.filesDir, "papertodo").apply { mkdirs() }
    private val documentDirectory = File(root, "document")
    private val stateFile get() = File(documentDirectory, "data.json")
    private val backupFile get() = File(documentDirectory, "data.backup.json")
    private val histories = mutableMapOf<String, History>()
    private var document = PaperDocument.empty()
    private var scheduledSave: ScheduledFuture<*>? = null
    private var saveVersion = 0L
    private var savedVersion = -1L
    private var blockedByCorruptState = false
    @Volatile private var latestSaveRequest: SaveRequest? = null

    @Volatile var startupWarning: String? = null
        private set
    @Volatile var lastSaveError: String? = null
        private set
    @Volatile var imageStore: ImageStore
        private set

    val isReadOnly: Boolean get() = synchronized(gate) { blockedByCorruptState }
    val papers: List<Paper> get() = synchronized(gate) { document.snapshots() }

    init {
        try { recoverImport() }
        catch (error: Exception) {
            blockedByCorruptState = true
            startupWarning = "上次导入未完成，原始数据已保留。请重新导入有效传输包。"
        }
        documentDirectory.mkdirs()
        if (!blockedByCorruptState) loadDocument()
        imageStore = ImageStore(applicationContext, File(documentDirectory, "images"))
        imageStore.autoCompressLargeImages = settingBoolean("autoCompressLargeImages", true)
        if (imageStore.warning != null) startupWarning = listOfNotNull(startupWarning, imageStore.warning).joinToString("\n")
        if (!blockedByCorruptState && !stateFile.exists() && !File(stateFile.path + ".bak").exists() && !backupFile.exists()) {
            document.paperArray.put(PaperDocument.newPaper("todo", "待办1"))
            enqueueSaveLocked(0)
        }
    }

    fun paper(id: String): Paper? = papers.firstOrNull { it.id == id }
    fun addListener(listener: () -> Unit) { listeners.add(listener) }
    fun removeListener(listener: () -> Unit) { listeners.remove(listener) }

    fun settingString(key: String, default: String = ""): String = synchronized(gate) { document.json.optString(key, default) }
    fun settingBoolean(key: String, default: Boolean = false): Boolean = synchronized(gate) { document.json.optBoolean(key, default) }
    fun settingDouble(key: String, default: Double = 1.0): Double = synchronized(gate) { document.json.optDouble(key, default) }

    fun setSetting(key: String, value: Any) {
        require(key != "papers" && (value is String || value is Boolean || value is Number))
        synchronized(gate) {
            ensureWritable()
            if (document.json.opt(key) == value) return
            document.json.put(key, value)
            if (key == "autoCompressLargeImages") imageStore.autoCompressLargeImages = value == true
            enqueueSaveLocked(0)
        }
        notifyChanged()
    }

    fun createPaper(type: String): String {
        val id: String
        synchronized(gate) {
            ensureWritable()
            val count = document.snapshots().count { it.type == type }
            val paper = PaperDocument.newPaper(type, (if (type == "note") "笔记" else "待办") + (count + 1))
            id = paper.getString("id")
            document.paperArray.put(paper)
            enqueueSaveLocked(0)
        }
        notifyChanged()
        return id
    }

    fun updateTitle(paperId: String, title: String) = mutate(paperId, "title") { it.put("title", title.take(500)) }
    fun updateContent(paperId: String, content: String) = mutate(paperId, "content") {
        if (content.toByteArray(Charsets.UTF_8).size > 8 * 1024 * 1024) throw IOException("单张笔记超过 8 MiB，请拆分后保存")
        it.put("content", content)
    }

    /** A multiline paste is exactly one history action, including new rows and prefix cleanup. */
    fun addTodo(paperId: String, text: String = ""): String {
        var firstId = ""
        mutate(paperId) { paper ->
            val list = todoObjects(paper)
            val lines = TodoTextSplitter.split(text).ifEmpty { listOf("") }
            lines.forEach { line ->
                val id = PaperDocument.newId()
                if (firstId.isEmpty()) firstId = id
                list.add(JSONObject().put("id", id).put("text", line).put("done", false).put("order", list.size))
            }
            putTodos(paper, list)
        }
        return firstId
    }

    fun updateTodoText(paperId: String, itemId: String, text: String) = mutate(paperId,
        if ('\n' in text || '\r' in text) null else "todo:$itemId") { paper ->
        val list = todoObjects(paper)
        val index = list.indexOfFirst { it.optString("id") == itemId }
        if (index >= 0) {
            if ('\n' in text || '\r' in text) {
                val lines = TodoTextSplitter.split(text).ifEmpty { listOf("") }
                list[index].put("text", lines.first())
                lines.drop(1).forEachIndexed { extraIndex, line ->
                    list.add(index + extraIndex + 1, JSONObject().put("id", PaperDocument.newId())
                        .put("text", line).put("done", false))
                }
            } else list[index].put("text", text)
            putTodos(paper, list)
        }
    }

    fun setTodoDone(paperId: String, itemId: String, done: Boolean) = mutate(paperId) { paper ->
        val list = todoObjects(paper)
        val item = list.firstOrNull { it.optString("id") == itemId } ?: return@mutate
        if (item.optBoolean("done") == done) return@mutate
        item.put("done", done)
        if (done && document.json.optBoolean("autoClearCompletedTodos", false)) list.remove(item)
        else if (document.json.optBoolean("autoMoveCompletedTodosToBottom", false)) {
            list.remove(item)
            val unfinished = list.filter { !it.optBoolean("done") }.toMutableList()
            val finished = list.filter { it.optBoolean("done") }.toMutableList()
            if (done) finished.add(item) else unfinished.add(item)
            list.clear(); list.addAll(unfinished); list.addAll(finished)
        }
        putTodos(paper, list)
    }

    fun deleteTodo(paperId: String, itemId: String) = mutate(paperId) { paper ->
        putTodos(paper, todoObjects(paper).filter { it.optString("id") != itemId })
    }

    fun clearCompletedTodos(paperId: String) = mutate(paperId) { paper ->
        putTodos(paper, todoObjects(paper).filter { !it.optBoolean("done") })
    }

    fun reorderTodos(paperId: String, orderedIds: List<String>) = mutate(paperId) { paper ->
        val items = todoObjects(paper)
        if (orderedIds.size != items.size || orderedIds.toSet() != items.map { it.getString("id") }.toSet()) return@mutate
        val byId = items.associateBy { it.getString("id") }
        putTodos(paper, orderedIds.map { byId.getValue(it) })
    }

    fun linkTodo(paperId: String, itemId: String, targetPaperId: String?) = mutate(paperId) { paper ->
        if (targetPaperId != null && document.paper(targetPaperId) == null) return@mutate
        todoObjects(paper).firstOrNull { it.optString("id") == itemId }?.let { item ->
            if (targetPaperId == null) item.remove("linkedNoteId") else item.put("linkedNoteId", targetPaperId)
        }
    }

    fun deletePaper(paperId: String) {
        synchronized(gate) {
            ensureWritable()
            val remaining = JSONArray()
            for (i in 0 until document.paperArray.length()) {
                val paper = document.paperArray.getJSONObject(i)
                if (paper.getString("id") == paperId) continue
                todoObjects(paper).forEach { if (it.optString("linkedNoteId") == paperId) it.remove("linkedNoteId") }
                remaining.put(paper)
            }
            document.json.put("papers", remaining)
            histories.remove(paperId)
            enqueueSaveLocked(0)
        }
        notifyChanged()
    }

    fun canUndo(paperId: String): Boolean = synchronized(gate) { histories[paperId]?.undo?.isNotEmpty() == true }
    fun canRedo(paperId: String): Boolean = synchronized(gate) { histories[paperId]?.redo?.isNotEmpty() == true }
    fun undo(paperId: String) = travelHistory(paperId, false)
    fun redo(paperId: String) = travelHistory(paperId, true)

    fun insertImage(uri: Uri, paperId: String): String {
        val imageId: String
        synchronized(gate) {
            ensureWritable()
            val paper = document.paper(paperId) ?: throw IOException("要插入图片的笔记已被删除")
            if (paper.optString("type") != "note") throw IOException("请在笔记纸片中插入图片")
            val content = paper.optString("content", "")
            if (content.toByteArray(Charsets.UTF_8).size + 64 > 8 * 1024 * 1024) {
                throw IOException("单张笔记超过 8 MiB，请拆分后再插入图片")
            }
            val before = paper.toString()
            imageId = imageStore.insert(uri, paperId)
            val separator = if (content.isEmpty() || content.endsWith('\n') || content.endsWith('\r')) "" else "\n"
            paper.put("content", content + separator + "![image|100%](i:$imageId)\n")
            recordHistoryLocked(paperId, before, null)
            enqueueSaveLocked(0)
        }
        notifyChanged()
        return imageId
    }

    fun imageFile(id: String): File? = imageStore.file(id)

    fun inspectImport(input: InputStream, isZip: Boolean): ImportPreview =
        TransferArchive.inspect(input, isZip, File(applicationContext.cacheDir, "paper-imports")) { file, metadata ->
            ImageStore.validateEncodedImage(file, metadata)
        }

    /** Whole-document replacement is staged first; a crash cannot splice old JSON and new images. */
    fun applyImport(preview: ImportPreview) {
        synchronized(gate) {
            if (preview.applied || !preview.stagingDirectory.isDirectory) throw IOException("导入预览已失效，请重新选择文件")
            val imported = PaperDocument.parse(preview.documentJson)
            flushLocked()
            val suffix = System.currentTimeMillis().toString() + "-" + PaperDocument.newId().take(8)
            val incoming = File(root, "document-incoming-$suffix")
            val previous = File(root, "document-before-import-$suffix")
            val marker = File(root, "pending-import.json")
            try {
                if (!incoming.mkdir()) throw IOException("无法创建导入目录")
                atomicWrite(File(incoming, "data.json"), imported.json.toString().toByteArray(Charsets.UTF_8))
                val imageDirectory = File(incoming, "images").apply { mkdir() }
                val meta = JSONArray()
                preview.images.forEach { image ->
                    val source = File(preview.stagingDirectory, "images/${image.id}")
                    if (!source.isFile || source.length() != image.byteLength.toLong() || !sha256(source.readBytes()).equals(image.sha256, true)) {
                        throw IOException("图片 ${image.id} 的导入缓存已改变，请重新选择文件")
                    }
                    source.copyTo(File(imageDirectory, image.id))
                    meta.put(image.toJson())
                }
                atomicWrite(File(imageDirectory, "meta.json"), meta.toString().toByteArray(Charsets.UTF_8))
                val transaction = JSONObject().put("incoming", incoming.name).put("previous", previous.name)
                atomicWrite(marker, transaction.toString().toByteArray(Charsets.UTF_8))
                synchronized(ioGate) {
                    if (documentDirectory.exists() && !documentDirectory.renameTo(previous)) throw IOException("无法备份当前数据")
                    if (!incoming.renameTo(documentDirectory)) {
                        previous.renameTo(documentDirectory)
                        throw IOException("无法启用新数据，已保留原数据")
                    }
                    // Invalidate any older non-blocking flush queued during staging before
                    // the writer can touch the newly installed directory.
                    val importedVersion = ++saveVersion
                    savedVersion = importedVersion
                    latestSaveRequest = SaveRequest(imported.json.toString(), importedVersion)
                }
                AtomicFile(marker).delete()
                document = imported
                blockedByCorruptState = false
                startupWarning = null
                lastSaveError = null
                histories.clear()
                imageStore = ImageStore(applicationContext, File(documentDirectory, "images"))
                imageStore.autoCompressLargeImages = document.json.optBoolean("autoCompressLargeImages", true)
                preview.applied = true
                // Keep the last three complete pre-import folders, including original damaged bytes.
                root.listFiles()?.filter { it.isDirectory && it.name.startsWith("document-before-import-") }
                    ?.sortedByDescending { it.name }?.drop(3)?.forEach { it.deleteRecursively() }
            } catch (error: Exception) {
                if (documentDirectory.exists()) {
                    incoming.deleteRecursively()
                    AtomicFile(marker).delete()
                } else {
                    blockedByCorruptState = true
                    startupWarning = "导入恢复尚未完成，已暂停写入；导入前数据和恢复标识均已保留。请重新启动后恢复。"
                }
                throw if (error is IOException) error else IOException("导入失败，原数据已保留", error)
            }
        }
        notifyChanged()
    }

    fun exportJson(output: OutputStream) {
        val text = synchronized(gate) {
            ensureWritable()
            document.json.toString(2)
        }
        output.write(text.toByteArray(Charsets.UTF_8))
        output.flush()
    }

    fun exportArchive(output: OutputStream) = synchronized(gate) {
        ensureWritable()
        TransferArchive.write(output, document.json.toString(), imageStore.allMetadata(), imageStore::file)
    }

    fun hasPreviousImportBackup(): Boolean = synchronized(gate) { previousImportDirectory() != null }

    /** Call on a worker thread: image hashes are checked before claiming a portable backup is valid. */
    fun previousImportBackupWarning(): String? = synchronized(gate) {
        val directory = previousImportDirectory() ?: return@synchronized "尚无导入前备份"
        try {
            readBackupContent(directory)
            null
        } catch (_: Exception) {
            "这份备份包含无法读取或损坏的原始数据，将导出为原始恢复包。恢复包保留文件供检查，不能当作有效传输包直接导入。"
        }
    }

    /** Valid backups are transfer ZIPs; damaged backups preserve every original byte in a recovery ZIP. */
    fun exportPreviousImport(output: OutputStream): Boolean = synchronized(gate) {
        val directory = previousImportDirectory() ?: return@synchronized false
        val content = try { readBackupContent(directory) } catch (_: Exception) { null }
        if (content != null) {
            TransferArchive.write(output, content.first, content.second) { id -> File(directory, "images/$id") }
        } else {
            ZipOutputStream(output).use { zip ->
                zip.putNextEntry(ZipEntry("manifest.json"))
                zip.write(JSONObject().put("format", "PaperTodoRecoveryBackup").put("version", 1)
                    .put("description", "导入前的原始文件，可能包含损坏数据；请先检查，不可直接作为传输包恢复。")
                    .toString().toByteArray(Charsets.UTF_8))
                zip.closeEntry()
                directory.walkTopDown().filter { it.isFile }.forEach { source ->
                    val relative = source.relativeTo(directory).invariantSeparatorsPath
                    zip.putNextEntry(ZipEntry("document/$relative"))
                    source.inputStream().use { it.copyTo(zip) }
                    zip.closeEntry()
                }
            }
        }
        true
    }

    fun flush() = synchronized(gate) { if (!blockedByCorruptState) flushLocked() }

    /** Lifecycle flush: never waits for a document/export lock on the calling UI thread. */
    fun requestFlush() {
        val request = latestSaveRequest ?: return
        try {
            writer.execute { saveSnapshot(request.payload, request.version) }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
            // close() already performs a synchronous final flush before shutting down.
        }
    }

    override fun close() {
        try { flush() }
        finally { writer.shutdown(); listeners.clear() }
    }

    private fun mutate(paperId: String, coalesceKey: String? = null, change: (JSONObject) -> Unit) {
        synchronized(gate) {
            ensureWritable()
            val paper = document.paper(paperId) ?: return
            val before = paper.toString()
            change(paper)
            if (paper.toString() == before) return
            recordHistoryLocked(paperId, before, coalesceKey)
            enqueueSaveLocked(if (coalesceKey == null) 0 else 300)
        }
        notifyChanged()
    }

    private fun travelHistory(paperId: String, redo: Boolean) {
        synchronized(gate) {
            ensureWritable()
            val history = histories[paperId] ?: return
            val source = if (redo) history.redo else history.undo
            val destination = if (redo) history.undo else history.redo
            val paper = document.paper(paperId) ?: return
            if (source.isEmpty()) return
            destination.addLast(paper.toString())
            document.replacePaper(paperId, JSONObject(source.removeLast()))
            history.lastKey = null
            enqueueSaveLocked(0)
        }
        notifyChanged()
    }

    private fun todoObjects(paper: JSONObject): MutableList<JSONObject> {
        val items = paper.optJSONArray("items") ?: JSONArray().also { paper.put("items", it) }
        return (0 until items.length()).map { items.getJSONObject(it) }.sortedBy { it.optInt("order") }.toMutableList()
    }

    private fun putTodos(paper: JSONObject, items: List<JSONObject>) {
        val array = JSONArray()
        items.forEachIndexed { index, item -> array.put(item.put("order", index)) }
        paper.put("items", array)
    }

    private fun enqueueSaveLocked(delay: Long) {
        if (blockedByCorruptState) return
        val payload = document.json.toString()
        val version = ++saveVersion
        latestSaveRequest = SaveRequest(payload, version)
        scheduledSave?.cancel(false)
        scheduledSave = writer.schedule({ saveSnapshot(payload, version) }, delay, TimeUnit.MILLISECONDS)
    }

    private fun flushLocked() {
        if (blockedByCorruptState) return
        scheduledSave?.cancel(false)
        val payload = document.json.toString()
        val version = ++saveVersion
        latestSaveRequest = SaveRequest(payload, version)
        writer.submit { saveSnapshot(payload, version) }.get(30, TimeUnit.SECONDS)
        lastSaveError?.let { throw IOException(it) }
    }

    private fun saveSnapshot(payload: String, version: Long) {
        synchronized(ioGate) {
            if (version <= savedVersion) return
            try {
                PaperDocument.parse(payload)
                if (stateFile.exists()) {
                    val old = stateFile.readText(Charsets.UTF_8)
                    try {
                        PaperDocument.parse(old)
                        atomicWrite(backupFile, old.toByteArray(Charsets.UTF_8))
                    } catch (_: Exception) { /* Never replace a good backup with a corrupt primary. */ }
                }
                atomicWrite(stateFile, payload.toByteArray(Charsets.UTF_8))
                savedVersion = version
                val hadError = lastSaveError != null
                lastSaveError = null
                if (hadError) notifyChanged()
            } catch (error: Exception) {
                lastSaveError = "自动保存失败，当前内容仍在内存中：${error.message ?: "存储不可用"}"
                notifyChanged()
            }
        }
    }

    private fun loadDocument() {
        val atomic = AtomicFile(stateFile)
        if (!stateFile.exists() && !File(stateFile.path + ".bak").exists()) {
            if (backupFile.exists()) {
                try {
                    document = PaperDocument.parse(backupFile.readText(Charsets.UTF_8))
                    startupWarning = "已从上一份有效备份恢复纸片。"
                    return
                } catch (_: Exception) { /* The originals remain untouched. */ }
            } else return
        }
        try {
            document = atomic.openRead().use { PaperDocument.parse(decodeUtf8(it.readBounded(PaperDocument.MAX_JSON_BYTES))) }
        } catch (error: Exception) {
            try {
                document = PaperDocument.parse(backupFile.readText(Charsets.UTF_8))
                if (stateFile.isFile) stateFile.copyTo(File(documentDirectory, "data.corrupt-${System.currentTimeMillis()}.json"))
                startupWarning = "主数据无法读取，已恢复上一份有效备份；损坏文件已保留。"
            } catch (_: Exception) {
                blockedByCorruptState = true
                startupWarning = "数据和备份均无法读取，已停止写入以保护原文件。请导入有效的电脑数据或传输包。"
            }
        }
    }

    private fun recoverImport() {
        val marker = File(root, "pending-import.json")
        if (!marker.exists() && !File(marker.path + ".bak").exists()) return
        val transaction = AtomicFile(marker).openRead().use { JSONObject(decodeUtf8(it.readBounded(16 * 1024))) }
        fun child(key: String, prefix: String): File {
            val name = transaction.getString(key)
            if (!Regex("$prefix[0-9]+-[0-9a-f]{8}").matches(name)) throw IOException("导入恢复标识无效")
            return File(root, name)
        }
        val incoming = child("incoming", "document-incoming-")
        val previous = child("previous", "document-before-import-")
        if (!documentDirectory.exists()) {
            if (previous.exists()) {
                if (!previous.renameTo(documentDirectory)) throw IOException("无法还原导入前的数据")
            } else if (incoming.exists()) {
                PaperDocument.parse(File(incoming, "data.json").readText(Charsets.UTF_8))
                if (!incoming.renameTo(documentDirectory)) throw IOException("无法恢复新数据")
            } else throw IOException("导入恢复文件缺失")
        }
        incoming.deleteRecursively()
        AtomicFile(marker).delete()
    }

    private fun ensureWritable() { if (blockedByCorruptState) throw IOException(startupWarning ?: "原数据已保护，暂不能修改") }

    private fun previousImportDirectory(): File? = root.listFiles()
        ?.filter { it.isDirectory && it.name.startsWith("document-before-import-") }?.maxByOrNull { it.name }

    private fun readBackupContent(directory: File): Pair<String, List<ImageMetadata>> {
        val json = File(directory, "data.json").inputStream().use { decodeUtf8(it.readBounded(PaperDocument.MAX_JSON_BYTES)) }
        PaperDocument.parse(json)
        val imageDirectory = File(directory, "images")
        val metadataFile = File(imageDirectory, "meta.json")
        val array = if (metadataFile.isFile) metadataFile.inputStream().use { parseMetadataArray(decodeUtf8(it.readBounded(8 * 1024 * 1024))) } else JSONArray()
        if (array.length() > ImageStore.MAX_IMAGES) throw IOException("图片数量过多")
        val images = (0 until array.length()).map { ImageMetadata.parse(array.getJSONObject(it)) }
        if (images.map { it.id }.toSet().size != images.size) throw IOException("图片 ID 重复")
        val blobs = imageDirectory.listFiles()?.filter { it.isFile && it.name != "meta.json" } ?: emptyList()
        if (blobs.map { it.name }.toSet() != images.map { it.id }.toSet()) throw IOException("图片索引与文件不匹配")
        if (images.sumOf { it.byteLength.toLong() } > ImageStore.MAX_TOTAL_BYTES) throw IOException("图片总量过大")
        images.forEach { image ->
            val file = File(imageDirectory, image.id)
            if (file.length() != image.byteLength.toLong() || !sha256(file.readBytes()).equals(image.sha256, true)) throw IOException("图片校验失败")
            ImageStore.validateEncodedImage(file, image)
        }
        return json to images
    }

    private fun notifyChanged() {
        if (Looper.myLooper() == Looper.getMainLooper()) listeners.forEach { it.invoke() }
        else main.post { listeners.forEach { it.invoke() } }
    }

    private class History {
        val undo = ArrayDeque<String>()
        val redo = ArrayDeque<String>()
        var lastKey: String? = null
        var lastTime = 0L
    }

    private fun recordHistoryLocked(paperId: String, before: String, coalesceKey: String?) {
        val history = histories.getOrPut(paperId) { History() }
        val now = System.nanoTime()
        if (coalesceKey == null || coalesceKey != history.lastKey || now - history.lastTime > 900_000_000L) {
            history.undo.addLast(before)
            while (history.undo.size > 100) history.undo.removeFirst()
        }
        history.lastKey = coalesceKey
        history.lastTime = now
        history.redo.clear()
    }

    private class SaveRequest(val payload: String, val version: Long)
}
