package io.github.papertodo.mobile.data

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.util.Base64
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class DataCompatibilityTest {
    @get:Rule val temporary = TemporaryFolder()

    private fun document(content: String = ""): String = JSONObject()
        .put("desktopFutureSetting", JSONObject().put("keep", 7))
        .put("papers", JSONArray().put(JSONObject().put("id", "paper-1").put("type", "note")
            .put("title", "中文便签").put("content", content).put("x", 1920.5)
            .put("capsuleMonitorDeviceName", "DISPLAY-SECONDARY")
            .put("futurePaperField", JSONArray().put("untouched"))
            .put("items", JSONArray().put(JSONObject().put("id", "item-1").put("text", "关联")
                .put("done", false).put("order", 0).put("linkedNoteId", "paper-2")
                .put("linkedPath", "D:\\folder\\file.md").put("futureTodoField", true)))))
        .toString()

    @Test fun `projecting data preserves all desktop and unknown fields`() {
        val original = JSONObject(document())
        val parsed = PaperDocument.parse(original.toString())
        val paper = parsed.snapshots().single()
        assertEquals("中文便签", paper.title)
        assertEquals("paper-2", paper.items.single().linkedNoteId)
        assertEquals("D:\\folder\\file.md", paper.items.single().linkedPath)
        assertEquals(7, parsed.json.getJSONObject("desktopFutureSetting").getInt("keep"))
        val retained = parsed.json.getJSONArray("papers").getJSONObject(0)
        assertEquals(1920.5, retained.getDouble("x"), 0.0)
        assertEquals("DISPLAY-SECONDARY", retained.getString("capsuleMonitorDeviceName"))
        assertTrue(retained.getJSONArray("items").getJSONObject(0).getBoolean("futureTodoField"))
        assertEquals("untouched", retained.getJSONArray("futurePaperField").getString(0))
    }

    @Test fun `duplicate paper and item identities are rejected before import`() {
        val root = JSONObject(document())
        root.getJSONArray("papers").put(JSONObject(root.getJSONArray("papers").getJSONObject(0).toString()))
        assertThrows(IOException::class.java) { PaperDocument.parse(root.toString()) }
        val second = JSONObject(document())
        val items = second.getJSONArray("papers").getJSONObject(0).getJSONArray("items")
        items.put(JSONObject(items.getJSONObject(0).toString()))
        assertThrows(IOException::class.java) { PaperDocument.parse(second.toString()) }
    }

    @Test fun `invalid structure wrong boolean and trailing JSON are rejected`() {
        assertThrows(IOException::class.java) { PaperDocument.parse("{\"papers\":null}") }
        assertThrows(IOException::class.java) { PaperDocument.parse(document() + " {}") }
        val root = JSONObject(document())
        root.getJSONArray("papers").getJSONObject(0).getJSONArray("items").getJSONObject(0).put("done", "false")
        assertThrows(IOException::class.java) { PaperDocument.parse(root.toString()) }
    }

    @Test fun `image references skip fenced examples and inline pseudo references`() {
        val content = """
            ![image|100%](i:001)
            ```markdown
            ![example](i:002)
            ````
            ~~~~
            ![example](i:003)
            ~~~~
            inline ![example](i:004)
            ![image|200x100](i:005) {width=50%}
            ![remote](https://example.invalid/picture.png)
        """.trimIndent()
        assertEquals(setOf("001", "005"), ImageReferences.ids(content))
        assertEquals("006", ImageReferences.idForLine("![label with ] bracket](i:006) {width=20%}"))
    }

    @Test fun `multiline paste cleans prefixes without empty tasks`() {
        assertEquals(listOf("第一项", "第二项", "第三项", "第四项"),
            TodoTextSplitter.split("- [ ] 第一项\r\n2. 第二项\n\n• 第三项\r+ 第四项"))
    }

    @Test fun `complete archive roundtrip retains original image and document fields`() {
        val png = Base64.getDecoder().decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWQAAAABJRU5ErkJggg==")
        val metadata = imageMetadata(png)
        val image = temporary.newFile("image")
        image.writeBytes(png)
        val output = ByteArrayOutputStream()
        TransferArchive.write(output, document("![image|100%](i:001)"), listOf(metadata)) { image }
        val preview = TransferArchive.inspect(ByteArrayInputStream(output.toByteArray()), true,
            temporary.newFolder("cache")) { _, _ -> }
        preview.use {
            assertEquals(1, it.paperCount)
            assertEquals(1, it.todoCount)
            assertEquals(1, it.imageCount)
            assertTrue(it.missingImageIds.isEmpty())
            assertArrayEquals(png, java.io.File(it.stagingDirectory, "images/001").readBytes())
            assertEquals("中文便签", PaperDocument.parse(it.documentJson).snapshots().single().title)
            assertEquals("preserved", it.images.single().toJson().getString("futureImageField"))
        }
        assertFalse(preview.stagingDirectory.exists())
    }

    @Test fun `text only import reports missing images without rewriting references`() {
        val preview = TransferArchive.inspect(ByteArrayInputStream(document("![image|100%](i:010)").toByteArray()), false,
            temporary.newFolder("json-cache")) { _, _ -> }
        preview.use {
            assertEquals(listOf("010"), it.missingImageIds)
            assertTrue(PaperDocument.parse(it.documentJson).snapshots().single().content.contains("i:010"))
        }
    }

    @Test fun `archive rejects tampered blobs missing blobs and unsafe entry names`() {
        val bytes = byteArrayOf(1, 2, 3)
        val metadata = JSONArray().put(imageMetadata(bytes).toJson()).toString()
        fun entries(blob: ByteArray? = bytes): LinkedHashMap<String, ByteArray> = linkedMapOf(
            "manifest.json" to "{\"format\":\"PaperTodoTransfer\",\"version\":1}".toByteArray(),
            "data.json" to document().toByteArray(), "images/meta.json" to metadata.toByteArray()
        ).also { if (blob != null) it["images/001"] = blob }
        assertBadArchive(entries(byteArrayOf(1, 2, 4)))
        assertBadArchive(entries(null))
        assertBadArchive(entries().also { it["../escape"] = bytes })
        assertBadArchive(entries().also { it["images/../../escape"] = bytes })
    }

    @Test fun `archive rejects duplicate metadata and unsupported versions`() {
        val bytes = byteArrayOf(1, 2, 3)
        val image = imageMetadata(bytes).toJson()
        val entries = linkedMapOf(
            "manifest.json" to "{\"format\":\"PaperTodoTransfer\",\"version\":1}".toByteArray(),
            "data.json" to document().toByteArray(),
            "images/meta.json" to JSONArray().put(image).put(image).toString().toByteArray(),
            "images/001" to bytes
        )
        assertBadArchive(entries)
        entries["images/meta.json"] = JSONArray().put(image).toString().toByteArray()
        entries["manifest.json"] = "{\"format\":\"PaperTodoTransfer\",\"version\":2}".toByteArray()
        assertBadArchive(entries)
    }

    @Test fun `metadata numeric strings and fractional dimensions are rejected`() {
        val image = imageMetadata(byteArrayOf(1)).toJson()
        image.put("width", "1")
        assertThrows(IOException::class.java) { ImageMetadata.parse(image) }
        image.put("width", 1.5)
        assertThrows(IOException::class.java) { ImageMetadata.parse(image) }
    }

    private fun imageMetadata(bytes: ByteArray): ImageMetadata = ImageMetadata.parse(JSONObject()
        .put("id", "001").put("noteId", "paper-1").put("mime", "image/png")
        .put("width", 1).put("height", 1).put("sha256", sha256(bytes)).put("byteLength", bytes.size)
        .put("createdAt", "2026-10-29T00:00:00Z").put("futureImageField", "preserved"))

    private fun assertBadArchive(entries: LinkedHashMap<String, ByteArray>) {
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip -> entries.forEach { (name, bytes) ->
            zip.putNextEntry(ZipEntry(name)); zip.write(bytes); zip.closeEntry()
        } }
        assertThrows(IOException::class.java) {
            TransferArchive.inspect(ByteArrayInputStream(output.toByteArray()), true,
                temporary.newFolder()) { _, _ -> }.close()
        }
    }
}
