package io.github.papertodo.mobile.data

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class DesktopInteroperabilityTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test fun `desktop LMDB export imports without losing metadata and exports back`() {
        val stream = javaClass.getResourceAsStream("/desktop-v1.zip") ?: error("Missing synthetic desktop fixture")
        val preview = stream.use {
            TransferArchive.inspect(it, true, temporary.newFolder("cache")) { _, _ -> }
        }
        preview.use {
            assertEquals(2, it.paperCount)
            assertEquals(1, it.todoCount)
            assertEquals(1, it.imageCount)
            assertEquals(listOf("998"), it.missingImageIds)
            val data = JSONObject(it.documentJson)
            assertEquals("原始字段", data.getJSONObject("futureSetting").getJSONArray("keep").getString(0))
            assertEquals(123.25, data.getJSONArray("papers").getJSONObject(0).getDouble("x"), 0.0)
            assertEquals("保留", data.getJSONArray("papers").getJSONObject(0).getString("futureField"))
            assertEquals("note-one", PaperDocument.parse(it.documentJson).snapshots()[1].items.single().linkedNoteId)
            val image = it.images.single()
            assertEquals("测试.png", image.originalName)
            assertEquals(2, image.width)
            assertEquals(3, image.height)
            val output = File("build/test-interop/android-v1.zip").apply { parentFile?.mkdirs() }
            output.outputStream().use { destination ->
                TransferArchive.write(destination, it.documentJson, it.images) { id -> File(preview.stagingDirectory, "images/$id") }
            }
            output.inputStream().use { exported ->
                TransferArchive.inspect(exported, true, temporary.newFolder("roundtrip")) { _, _ -> }.use { checked ->
                    assertEquals(it.documentJson, checked.documentJson)
                    assertEquals(image.sha256, checked.images.single().sha256)
                }
            }
        }
    }
}
