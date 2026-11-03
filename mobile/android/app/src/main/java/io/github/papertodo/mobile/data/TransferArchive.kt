package io.github.papertodo.mobile.data

import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.security.MessageDigest
import java.time.OffsetDateTime
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream

class ImageMetadata internal constructor(
    val id: String,
    val noteId: String,
    val mime: String,
    val width: Int,
    val height: Int,
    val sha256: String,
    val byteLength: Int,
    val createdAt: String,
    val originalName: String?,
    internal val originalJson: String
) {
    internal fun toJson(): JSONObject = JSONObject(originalJson)

    companion object {
        internal val ID = Regex("[0-9]{3,8}")
        internal val MIMES = setOf("image/png", "image/jpeg", "image/gif", "image/bmp", "image/tiff", "image/webp")

        internal fun parse(json: JSONObject): ImageMetadata {
            try {
                fun string(key: String): String = json.opt(key) as? String ?: throw IOException("图片字段 $key 格式错误")
                fun integer(key: String): Int {
                    val value = json.opt(key) as? Number ?: throw IOException("图片字段 $key 格式错误")
                    val number = value.toDouble()
                    if (!number.isFinite() || number != value.toInt().toDouble()) throw IOException("图片字段 $key 应为整数")
                    return value.toInt()
                }
                val id = string("id")
                val noteId = string("noteId")
                val mime = string("mime")
                val width = integer("width")
                val height = integer("height")
                val length = integer("byteLength")
                val hash = string("sha256")
                val createdAt = string("createdAt")
                if (!ID.matches(id)) throw IOException("图片 ID 无效")
                if (noteId.isBlank() || mime !in MIMES || width !in 1..4096 || height !in 1..4096 ||
                    length !in 1..ImageStore.MAX_IMAGE_BYTES || !hash.matches(Regex("[0-9a-fA-F]{64}")) || createdAt.isBlank()) {
                    throw IOException("图片 $id 的元数据无效")
                }
                try { OffsetDateTime.parse(createdAt) }
                catch (error: Exception) { throw IOException("图片 $id 的创建时间无效", error) }
                if (json.has("originalName") && !json.isNull("originalName") && json.opt("originalName") !is String) {
                    throw IOException("图片文件名格式错误")
                }
                return ImageMetadata(id, noteId, mime, width, height, hash, length, createdAt,
                    json.nullableString("originalName"), json.toString())
            } catch (error: IOException) { throw error }
            catch (error: Exception) { throw IOException("图片元数据格式错误", error) }
        }
    }
}

/** A validated, private cache copy. Closing it never touches the live document. */
class ImportPreview internal constructor(
    val paperCount: Int,
    val todoCount: Int,
    val imageCount: Int,
    val missingImageIds: List<String>,
    internal val documentJson: String,
    internal val images: List<ImageMetadata>,
    internal val stagingDirectory: File
) : AutoCloseable {
    internal var applied = false
    override fun close() { stagingDirectory.deleteRecursively() }
}

internal object TransferArchive {
    private const val MAX_META_BYTES = 8 * 1024 * 1024
    private const val MAX_ENTRIES = 20003

    fun inspect(input: InputStream, isZip: Boolean, cacheDirectory: File,
                validateImage: (File, ImageMetadata) -> Unit): ImportPreview {
        cacheDirectory.mkdirs()
        val stage = File.createTempFile("paper-import-", "", cacheDirectory)
        if (!stage.delete() || !stage.mkdir()) throw IOException("无法准备导入文件")
        try {
            val imageDirectory = File(stage, "images").apply { mkdir() }
            val document: PaperDocument
            val images: List<ImageMetadata>
            if (!isZip) {
                document = PaperDocument.parse(decodeUtf8(input.readBounded(PaperDocument.MAX_JSON_BYTES)))
                images = emptyList()
            } else {
                var data: String? = null
                var manifest: JSONObject? = null
                var metadataArray: JSONArray? = null
                val entries = mutableSetOf<String>()
                val blobs = mutableSetOf<String>()
                var totalBytes = 0L
                ZipInputStream(input).use { zip ->
                    while (true) {
                        val entry = zip.nextEntry ?: break
                        if (entries.size >= MAX_ENTRIES || !entries.add(entry.name)) throw IOException("传输包包含重复或过多条目")
                        if (entry.isDirectory) throw IOException("传输包包含未知目录")
                        when (entry.name) {
                            "manifest.json" -> {
                                val text = decodeUtf8(zip.readBounded(4 * 1024))
                                val tokener = JSONTokener(text.removePrefix("\uFEFF"))
                                manifest = tokener.nextValue() as? JSONObject ?: throw IOException("传输包标识格式错误")
                                if (tokener.nextClean() != '\u0000') throw IOException("传输包标识格式错误")
                            }
                            "data.json" -> data = decodeUtf8(zip.readBounded(PaperDocument.MAX_JSON_BYTES))
                            "images/meta.json" -> {
                                val text = decodeUtf8(zip.readBounded(MAX_META_BYTES))
                                val tokener = JSONTokener(text.removePrefix("\uFEFF"))
                                metadataArray = tokener.nextValue() as? JSONArray ?: throw IOException("图片索引格式错误")
                                if (tokener.nextClean() != '\u0000') throw IOException("图片索引格式错误")
                            }
                            else -> {
                                val match = Regex("images/([0-9]{3,8})").matchEntire(entry.name)
                                    ?: throw IOException("传输包包含未知或不安全的路径")
                                val id = match.groupValues[1]
                                val bytes = zip.readBounded(ImageStore.MAX_IMAGE_BYTES)
                                totalBytes += bytes.size
                                if (totalBytes > ImageStore.MAX_TOTAL_BYTES) throw IOException("图片总量超过 120 MiB")
                                File(imageDirectory, id).writeBytes(bytes)
                                blobs.add(id)
                            }
                        }
                        zip.closeEntry()
                    }
                }
                val header = manifest ?: throw IOException("传输包缺少 manifest.json")
                if (header.optString("format") != "PaperTodoTransfer" || header.opt("version") !is Number ||
                    (header.opt("version") as Number).toDouble() != 1.0) {
                    throw IOException("不支持此传输包版本")
                }
                document = PaperDocument.parse(data ?: throw IOException("传输包缺少 data.json"))
                val array = metadataArray ?: throw IOException("传输包缺少图片索引")
                images = (0 until array.length()).map { index ->
                    ImageMetadata.parse(array.optJSONObject(index) ?: throw IOException("图片索引格式错误"))
                }
                val imageIds = images.map { it.id }.toSet()
                if (imageIds.size != images.size || imageIds != blobs) throw IOException("图片索引与文件不一致或 ID 重复")
                for (image in images) {
                    val file = File(imageDirectory, image.id)
                    if (file.length() != image.byteLength.toLong() ||
                        !sha256(file.readBytes()).equals(image.sha256, ignoreCase = true)) {
                        throw IOException("图片 ${image.id} 校验失败")
                    }
                    validateImage(file, image)
                }
            }
            val papers = document.snapshots()
            return ImportPreview(papers.size, papers.sumOf { it.items.size }, images.size,
                (document.referencedImageIds() - images.map { it.id }.toSet()).sorted(),
                document.json.toString(), images, stage)
        } catch (error: Exception) {
            stage.deleteRecursively()
            if (error is IOException) throw error
            throw IOException("无法读取传输包：${error.message}", error)
        }
    }

    fun write(output: OutputStream, documentJson: String, images: List<ImageMetadata>, imageFile: (String) -> File?) {
        PaperDocument.parse(documentJson)
        ZipOutputStream(output).use { zip ->
            fun entry(name: String, bytes: ByteArray) {
                zip.putNextEntry(ZipEntry(name))
                zip.write(bytes)
                zip.closeEntry()
            }
            entry("manifest.json", JSONObject().put("format", "PaperTodoTransfer").put("version", 1).toString().toByteArray(Charsets.UTF_8))
            entry("data.json", documentJson.toByteArray(Charsets.UTF_8))
            val meta = JSONArray()
            images.forEach { meta.put(it.toJson()) }
            entry("images/meta.json", meta.toString().toByteArray(Charsets.UTF_8))
            images.forEach { image ->
                val file = imageFile(image.id) ?: throw IOException("找不到图片 ${image.id}")
                val bytes = file.readBytes()
                if (bytes.size != image.byteLength || !sha256(bytes).equals(image.sha256, true)) throw IOException("图片 ${image.id} 已损坏")
                entry("images/${image.id}", bytes)
            }
        }
    }
}

internal fun InputStream.readBounded(maximum: Int): ByteArray {
    val output = java.io.ByteArrayOutputStream(minOf(maximum, 16 * 1024))
    val buffer = ByteArray(16 * 1024)
    var count = 0
    while (true) {
        val read = read(buffer)
        if (read < 0) break
        count += read
        if (count > maximum) throw IOException("文件或传输包条目过大")
        output.write(buffer, 0, read)
    }
    return output.toByteArray()
}

internal fun decodeUtf8(bytes: ByteArray): String = try {
    Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
        .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
} catch (error: Exception) { throw IOException("文件不是有效的 UTF-8", error) }

internal fun parseMetadataArray(text: String): JSONArray {
    try {
        val tokener = JSONTokener(text.removePrefix("\uFEFF"))
        val array = tokener.nextValue() as? JSONArray ?: throw IOException("图片索引格式错误")
        if (tokener.nextClean() != '\u0000') throw IOException("图片索引后存在多余内容")
        return array
    } catch (error: IOException) { throw error }
    catch (error: Exception) { throw IOException("图片索引格式错误", error) }
}

internal fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
    .digest(bytes).joinToString("") { "%02x".format(it.toInt() and 0xff) }
