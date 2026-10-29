package io.github.papertodo.mobile.data

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import androidx.exifinterface.media.ExifInterface
import android.net.Uri
import android.provider.OpenableColumns
import android.util.AtomicFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.time.Instant
import kotlin.math.max
import kotlin.math.roundToInt

/** Originals and metadata are local files; numeric i: identifiers remain desktop-compatible. */
class ImageStore internal constructor(private val context: Context, internal val directory: File) {
    private val gate = Any()
    private val images = linkedMapOf<String, ImageMetadata>()
    private var writeDisabled = false
    var autoCompressLargeImages: Boolean = true
    var warning: String? = null
        private set

    init {
        directory.mkdirs()
        val index = File(directory, "meta.json")
        if (index.exists() || File(index.path + ".bak").exists()) {
            try {
                val array = AtomicFile(index).openRead().use { parseMetadataArray(decodeUtf8(it.readBounded(8 * 1024 * 1024))) }
                if (array.length() > MAX_IMAGES) throw IOException("图片数量超过传输包限制")
                var total = 0L
                for (i in 0 until array.length()) {
                    val image = ImageMetadata.parse(array.getJSONObject(i))
                    if (images.put(image.id, image) != null) throw IOException("图片 ID 重复")
                    val imageFile = File(directory, image.id)
                    if (!imageFile.isFile || imageFile.length() != image.byteLength.toLong()) throw IOException("图片 ${image.id} 缺失或损坏")
                    total += image.byteLength
                    if (total > MAX_TOTAL_BYTES) throw IOException("图片总量超限")
                }
            } catch (error: Exception) {
                writeDisabled = true
                warning = "图片索引无法完整读取，已暂停新增图片。请先导出备份或重新导入完整传输包。"
            }
        }
    }

    fun metadata(id: String): ImageMetadata? = synchronized(gate) { images[id] }
    fun allMetadata(): List<ImageMetadata> = synchronized(gate) { images.values.toList() }
    fun file(id: String): File? = synchronized(gate) {
        if (!ImageMetadata.ID.matches(id) || images[id] == null) null else File(directory, id).takeIf { it.isFile }
    }

    fun insert(uri: Uri, noteId: String): String = synchronized(gate) {
        if (writeDisabled) throw IOException(warning ?: "图片库已暂停写入")
        if (noteId.isBlank()) throw IOException("找不到要插入图片的笔记")
        if (images.size >= MAX_IMAGES) throw IOException("图片数量已达 20000 张，请先导出并清理笔记")
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBounded(MAX_INPUT_BYTES) }
            ?: throw IOException("无法打开这张图片")
        val prepared = prepare(bytes)
        val currentBytes = images.values.sumOf { it.byteLength.toLong() }
        if (currentBytes + prepared.bytes.size > MAX_TOTAL_BYTES) throw IOException("图片总量已达 120 MiB，请先导出备份并清理笔记")
        // Include orphan files left by an interrupted insertion, so IDs are never reused accidentally.
        val largest = (images.keys + (directory.list()?.filter { ImageMetadata.ID.matches(it) } ?: emptyList()))
            .maxOfOrNull { it.toLong() } ?: 0L
        if (largest >= 99_999_999L) throw IOException("图片 ID 已用完")
        val id = (largest + 1L).toString().padStart(3, '0')
        val name = try {
            context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) cursor.getString(0) else null
            }
        } catch (_: Exception) { null }
        val json = JSONObject().put("id", id).put("noteId", noteId).put("mime", prepared.mime)
            .put("width", prepared.width).put("height", prepared.height)
            .put("sha256", sha256(prepared.bytes)).put("byteLength", prepared.bytes.size)
            .put("createdAt", Instant.now().toString())
        name?.let { json.put("originalName", it.take(512)) }
        val image = ImageMetadata.parse(json)
        val target = File(directory, id)
        atomicWrite(target, prepared.bytes)
        images[id] = image
        try { saveIndex() }
        catch (error: Exception) {
            images.remove(id)
            target.delete()
            throw IOException("图片保存失败，未修改笔记", error)
        }
        id
    }

    private fun saveIndex() {
        val array = JSONArray()
        images.values.forEach { array.put(it.toJson()) }
        val bytes = array.toString().toByteArray(Charsets.UTF_8)
        if (bytes.size > 8 * 1024 * 1024) throw IOException("图片索引过大，请先导出备份")
        atomicWrite(File(directory, "meta.json"), bytes)
    }

    private data class Prepared(val bytes: ByteArray, val mime: String, val width: Int, val height: Int)

    private fun prepare(bytes: ByteArray): Prepared {
        if (bytes.isEmpty()) throw IOException("图片为空")
        val mime = sniffMime(bytes) ?: throw IOException("请选择 PNG、JPEG、GIF、BMP 或 WebP 图片")
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) throw IOException("手机无法解码此图片，请先转换为 PNG 或 JPEG")
        val mustCompress = bytes.size > MAX_IMAGE_BYTES || max(bounds.outWidth, bounds.outHeight) > 4096
        val preferCompress = autoCompressLargeImages && max(bounds.outWidth, bounds.outHeight) > 2048
        if (mustCompress && !autoCompressLargeImages) throw IOException("图片超过 8 MiB 或最长边 4096 像素，请开启自动压缩")
        val orientation = try {
            ExifInterface(ByteArrayInputStream(bytes)).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)
        } catch (_: Exception) { ExifInterface.ORIENTATION_NORMAL }
        if (!mustCompress && !preferCompress && orientation in listOf(ExifInterface.ORIENTATION_NORMAL, ExifInterface.ORIENTATION_UNDEFINED)) {
            return Prepared(bytes, mime, bounds.outWidth, bounds.outHeight)
        }
        if (mime == "image/gif" || mime == "image/tiff") throw IOException("此 GIF/TIFF 图片过大，请先缩小后再插入，以保留原始图像")
        var bitmap: Bitmap? = null
        try {
            val targetDimension = if (autoCompressLargeImages) 2048 else 4096
            var sample = 1
            while (max(bounds.outWidth, bounds.outHeight) / (sample * 2) >= targetDimension) sample *= 2
            bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
                ?: throw IOException("无法解码图片")
            val matrix = Matrix()
            when (orientation) {
                ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> matrix.setScale(-1f, 1f)
                ExifInterface.ORIENTATION_ROTATE_180 -> matrix.setRotate(180f)
                ExifInterface.ORIENTATION_FLIP_VERTICAL -> matrix.setScale(1f, -1f)
                ExifInterface.ORIENTATION_TRANSPOSE -> { matrix.setRotate(90f); matrix.postScale(-1f, 1f) }
                ExifInterface.ORIENTATION_ROTATE_90 -> matrix.setRotate(90f)
                ExifInterface.ORIENTATION_TRANSVERSE -> { matrix.setRotate(270f); matrix.postScale(-1f, 1f) }
                ExifInterface.ORIENTATION_ROTATE_270 -> matrix.setRotate(270f)
            }
            if (!matrix.isIdentity) {
                val rotated = Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
                if (rotated !== bitmap) { bitmap.recycle(); bitmap = rotated }
            }
            if (max(bitmap.width, bitmap.height) > targetDimension) {
                val scale = targetDimension.toDouble() / max(bitmap.width, bitmap.height)
                val resized = Bitmap.createScaledBitmap(bitmap, max(1, (bitmap.width * scale).roundToInt()),
                    max(1, (bitmap.height * scale).roundToInt()), true)
                if (resized !== bitmap) { bitmap.recycle(); bitmap = resized }
            }
            val outputMime = if (mime == "image/jpeg" || !bitmap.hasAlpha()) "image/jpeg" else "image/png"
            while (true) {
                val current = bitmap ?: throw IOException("无法读取压缩中的图片")
                val output = ByteArrayOutputStream()
                val format = if (outputMime == "image/jpeg") Bitmap.CompressFormat.JPEG else Bitmap.CompressFormat.PNG
                if (!current.compress(format, 90, output)) throw IOException("图片压缩失败")
                val encoded = output.toByteArray()
                if (encoded.size <= MAX_IMAGE_BYTES) return Prepared(encoded, outputMime, current.width, current.height)
                if (!autoCompressLargeImages || max(current.width, current.height) < 256) throw IOException("压缩后图片仍超过 8 MiB")
                val resized = Bitmap.createScaledBitmap(current, max(1, (current.width * .75).roundToInt()),
                    max(1, (current.height * .75).roundToInt()), true)
                current.recycle()
                bitmap = resized
            }
        } catch (error: OutOfMemoryError) {
            throw IOException("图片像素过大，手机内存不足，请先缩小图片", error)
        } finally { bitmap?.recycle() }
    }

    companion object {
        const val MAX_IMAGE_BYTES = 8 * 1024 * 1024
        const val MAX_TOTAL_BYTES = 120L * 1024 * 1024
        const val MAX_IMAGES = 20000
        private const val MAX_INPUT_BYTES = 32 * 1024 * 1024

        internal fun validateEncodedImage(file: File, metadata: ImageMetadata) {
            val bytes = file.readBytes()
            if (sniffMime(bytes) != metadata.mime) throw IOException("图片 ${metadata.id} 格式与索引不符")
            val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
            // TIFF is portable in the archive even on devices without a TIFF decoder.
            if (options.outWidth > 0 && options.outHeight > 0) {
                if (options.outWidth != metadata.width || options.outHeight != metadata.height) throw IOException("图片 ${metadata.id} 尺寸与索引不符")
            } else if (metadata.mime != "image/tiff") throw IOException("图片 ${metadata.id} 无法解码")
        }

        internal fun sniffMime(bytes: ByteArray): String? {
            fun begins(vararg values: Int): Boolean = bytes.size >= values.size && values.indices.all { (bytes[it].toInt() and 255) == values[it] }
            fun ascii(start: Int, text: String): Boolean = bytes.size >= start + text.length && text.indices.all { bytes[start + it].toInt() == text[it].code }
            return when {
                begins(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a) -> "image/png"
                begins(0xff, 0xd8, 0xff) -> "image/jpeg"
                ascii(0, "GIF87a") || ascii(0, "GIF89a") -> "image/gif"
                ascii(0, "BM") -> "image/bmp"
                begins(0x49, 0x49, 0x2a, 0) || begins(0x4d, 0x4d, 0, 0x2a) -> "image/tiff"
                ascii(0, "RIFF") && ascii(8, "WEBP") -> "image/webp"
                else -> null
            }
        }
    }
}

internal fun atomicWrite(file: File, bytes: ByteArray) {
    file.parentFile?.mkdirs()
    val atomic = AtomicFile(file)
    val output = atomic.startWrite()
    try { output.write(bytes); atomic.finishWrite(output) }
    catch (error: Throwable) { atomic.failWrite(output); throw error }
}
