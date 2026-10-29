package io.github.papertodo.mobile.data

import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.OutputStream
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.ZipInputStream

/** Exercises actual Android AtomicFile, background writes, startup and whole-library imports. */
class PaperRepositoryInstrumentedTest {
    private lateinit var directory: File
    private lateinit var testContext: Context
    private val repositories = mutableListOf<PaperRepository>()

    @Before fun prepare() {
        val target = InstrumentationRegistry.getInstrumentation().targetContext
        directory = File(target.cacheDir, "repository-test-${System.nanoTime()}").apply { mkdirs() }
        testContext = object : ContextWrapper(target) {
            override fun getApplicationContext(): Context = this
            override fun getFilesDir(): File = File(directory, "files").apply { mkdirs() }
            override fun getCacheDir(): File = File(directory, "cache").apply { mkdirs() }
        }
    }

    @After fun clean() {
        repositories.forEach { it.close() }
        directory.deleteRecursively()
    }

    private fun repository(): PaperRepository = PaperRepository(testContext).also { repositories.add(it) }
    private fun stateDirectory(): File = File(testContext.filesDir, "papertodo/document").apply { mkdirs() }

    @Test fun multilinePasteAndCompletionAreSingleUndoActions() {
        val repository = repository()
        val id = repository.papers.single().id
        repository.addTodo(id, "- 第一项\n2. 第二项\n[x] 第三项")
        assertEquals(listOf("第一项", "第二项", "第三项"), repository.paper(id)!!.items.map { it.text })
        repository.undo(id)
        assertTrue(repository.paper(id)!!.items.isEmpty())
        repository.redo(id)
        val firstItem = repository.paper(id)!!.items.first().id
        repository.setSetting("autoClearCompletedTodos", true)
        repository.setTodoDone(id, firstItem, true)
        assertEquals(2, repository.paper(id)!!.items.size)
        repository.undo(id)
        assertEquals(3, repository.paper(id)!!.items.size)
        assertFalse(repository.paper(id)!!.items.first().done)
        repository.flush()
        val saved = JSONObject(File(stateDirectory(), "data.json").readText())
        assertEquals(3, saved.getJSONArray("papers").getJSONObject(0).getJSONArray("items").length())
    }

    @Test fun importingThenEditingPreservesUnknownFieldsAndDesktopCoordinates() {
        val repository = repository()
        val fixture = """{"futureSetting":{"keep":7},"papers":[{"id":"note","type":"note","title":"原题","content":"原文","x":912.5,"futureField":true,"items":[]}]}"""
        repository.inspectImport(ByteArrayInputStream(fixture.toByteArray()), false).use { repository.applyImport(it) }
        repository.updateTitle("note", "新标题")
        repository.updateContent("note", "中文新内容")
        repository.flush()
        val output = ByteArrayOutputStream()
        repository.exportJson(output)
        val json = JSONObject(output.toString("UTF-8"))
        assertEquals(7, json.getJSONObject("futureSetting").getInt("keep"))
        val note = json.getJSONArray("papers").getJSONObject(0)
        assertEquals(912.5, note.getDouble("x"), 0.0)
        assertTrue(note.getBoolean("futureField"))
        assertEquals("中文新内容", note.getString("content"))
        val backupDirectories = File(testContext.filesDir, "papertodo").listFiles()!!.filter { it.name.startsWith("document-before-import-") }
        assertEquals(1, backupDirectories.size)
        assertTrue(File(backupDirectories.single(), "data.json").isFile)
        assertTrue(repository.hasPreviousImportBackup())
        assertNull(repository.previousImportBackupWarning())
        val exportedBackup = ByteArrayOutputStream()
        assertTrue(repository.exportPreviousImport(exportedBackup))
        repository.inspectImport(ByteArrayInputStream(exportedBackup.toByteArray()), true).use {
            assertEquals(1, it.paperCount)
            assertEquals(0, it.imageCount)
        }
    }

    @Test fun corruptedStartupProtectsOriginalAndCanImportRecovery() {
        val file = File(stateDirectory(), "data.json")
        val damaged = "{not-valid-json"
        file.writeText(damaged)
        val repository = repository()
        assertTrue(repository.isReadOnly)
        assertTrue(repository.papers.isEmpty())
        assertNotNull(repository.startupWarning)
        repository.flush()
        assertEquals(damaged, file.readText())
        try {
            repository.createPaper("note")
            fail("Corrupt startup must not overwrite existing data")
        } catch (_: IOException) { }
        val valid = JSONObject().put("papers", JSONArray()).toString()
        repository.inspectImport(ByteArrayInputStream(valid.toByteArray()), false).use { repository.applyImport(it) }
        assertFalse(repository.isReadOnly)
        val id = repository.createPaper("note")
        repository.updateContent(id, "恢复后正常保存")
        repository.flush()
        assertEquals("恢复后正常保存", repository.paper(id)!!.content)
        val originals = File(testContext.filesDir, "papertodo").listFiles()!!.filter { it.name.startsWith("document-before-import-") }
        assertEquals(damaged, File(originals.single(), "data.json").readText())
        assertNotNull(repository.previousImportBackupWarning())
        val recovery = ByteArrayOutputStream()
        assertTrue(repository.exportPreviousImport(recovery))
        val entries = mutableMapOf<String, String>()
        ZipInputStream(ByteArrayInputStream(recovery.toByteArray())).use { zip ->
            while (true) {
                val entry = zip.nextEntry ?: break
                entries[entry.name] = zip.readBytes().toString(Charsets.UTF_8)
                zip.closeEntry()
            }
        }
        assertEquals(damaged, entries["document/data.json"])
        assertEquals("PaperTodoRecoveryBackup", JSONObject(entries.getValue("manifest.json")).getString("format"))
        try {
            repository.inspectImport(ByteArrayInputStream(recovery.toByteArray()), true).close()
            fail("Damaged recovery package must not be advertised as an importable transfer")
        } catch (_: IOException) { }
    }

    @Test fun missingPrimaryRestoresBackupWithoutAddingDefaultPaper() {
        val valid = """{"papers":[{"id":"restored","type":"note","title":"备份","content":"原文"}]}"""
        File(stateDirectory(), "data.backup.json").writeText(valid)
        val repository = repository()
        assertFalse(repository.isReadOnly)
        assertEquals(listOf("restored"), repository.papers.map { it.id })
        repository.updateContent("restored", "修改")
        repository.flush()
        assertEquals("修改", JSONObject(File(stateDirectory(), "data.json").readText()).getJSONArray("papers").getJSONObject(0).getString("content"))
    }

    @Test fun completedOrderingMovesToggledTaskToEndOfItsGroup() {
        val repository = repository()
        val paper = repository.papers.single().id
        repository.addTodo(paper, "A\nB\nC")
        repository.setSetting("autoMoveCompletedTodosToBottom", true)
        val b = repository.paper(paper)!!.items[1].id
        repository.setTodoDone(paper, b, true)
        assertEquals(listOf("A", "C", "B"), repository.paper(paper)!!.items.map { it.text })
        repository.setTodoDone(paper, b, false)
        assertEquals(listOf("A", "C", "B"), repository.paper(paper)!!.items.map { it.text })
        assertTrue(repository.paper(paper)!!.items.none { it.done })
    }

    @Test fun desktopImageArchiveRoundTripsThroughActualAndroidDecoder() {
        val repository = repository()
        val fixture = InstrumentationRegistry.getInstrumentation().context.assets.open("desktop-v1.zip")
        fixture.use { input -> repository.inspectImport(input, true).use { preview ->
            assertEquals(listOf("998"), preview.missingImageIds)
            assertEquals(1, preview.imageCount)
            repository.applyImport(preview)
        } }
        assertEquals("测试.png", repository.imageStore.metadata("001")!!.originalName)
        assertEquals(2, repository.imageStore.metadata("001")!!.width)
        assertEquals(3, repository.imageStore.metadata("001")!!.height)
        val exported = ByteArrayOutputStream()
        repository.exportArchive(exported)
        repository.inspectImport(ByteArrayInputStream(exported.toByteArray()), true).use { preview ->
            assertEquals(2, preview.paperCount)
            assertEquals(1, preview.imageCount)
            assertEquals(listOf("998"), preview.missingImageIds)
        }
        assertEquals(setOf("001"), ImageReferences.ids("![label with ] bracket](i:001) {width=50%}\n```\n![sample](i:002)\n```"))
        assertEquals(listOf("第一项", "第二项"), TodoTextSplitter.split("- [ ] 第一项\n2. 第二项"))
    }

    @Test fun lifecycleFlushDoesNotWaitForAnExportHoldingTheDocumentLock() {
        val repository = repository()
        repository.flush()
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val firstWrite = AtomicBoolean(true)
        val failure = AtomicReference<Throwable?>(null)
        val exporter = Thread {
            try {
                repository.exportArchive(object : OutputStream() {
                    override fun write(value: Int) {
                        if (firstWrite.compareAndSet(true, false)) {
                            entered.countDown()
                            release.await(2, TimeUnit.SECONDS)
                        }
                    }
                })
            } catch (error: Throwable) { failure.set(error) }
        }
        exporter.start()
        try {
            assertTrue(entered.await(2, TimeUnit.SECONDS))
            val start = System.nanoTime()
            repository.requestFlush()
            assertTrue("Lifecycle flush must not wait for the document/export lock", System.nanoTime() - start < 500_000_000L)
        } finally {
            release.countDown()
            exporter.join(3000)
        }
        assertFalse(exporter.isAlive)
        assertNull(failure.get())
    }

    @Test fun insertingImageAppendsReferenceAndIsOneUndoAction() {
        val repository = repository()
        val noteId = repository.createPaper("note")
        repository.updateContent(noteId, "保留原文")
        val fixtureImage = File(testContext.cacheDir, "fixture.png")
        InstrumentationRegistry.getInstrumentation().context.assets.open("desktop-v1.zip").use { input ->
            ZipInputStream(input).use { zip ->
                while (true) {
                    val entry = zip.nextEntry ?: break
                    if (entry.name == "images/001") fixtureImage.writeBytes(zip.readBytes())
                    zip.closeEntry()
                }
            }
        }
        val imageId = repository.insertImage(Uri.fromFile(fixtureImage), noteId)
        assertEquals("001", imageId)
        assertEquals("保留原文\n![image|100%](i:001)\n", repository.paper(noteId)!!.content)
        assertNotNull(repository.imageFile(imageId))
        repository.undo(noteId)
        assertEquals("保留原文", repository.paper(noteId)!!.content)
        assertNotNull(repository.imageFile(imageId))
        repository.redo(noteId)
        assertTrue(repository.paper(noteId)!!.content.contains("i:001"))
        repository.flush()
    }
}
