import unittest

from release_notes import release_section, render_notes


class ReleaseNotesTests(unittest.TestCase):
    def test_exact_version_without_prefix_collision(self):
        markdown = "### v3.31\n\nnew\n\n---\n\n### v3.3\n\nold\n\n### Unreleased\n\nfuture"
        self.assertEqual(release_section(markdown, "v3.3"), "old")
        self.assertEqual(release_section(markdown, "v3.31"), "new")

    def test_chinese_heading_label_and_crlf(self):
        self.assertEqual(release_section("### v3.0 正式版\r\n\r\n原文\r\n", "v3.0"), "原文")

    def test_missing_empty_or_duplicate_sections_fail(self):
        for markdown in ("### v3.31\ntext", "### v3.3\n\n---\n### v3.31\ntext", "### v3.3\na\n### v3.3\nb"):
            with self.subTest(markdown=markdown), self.assertRaises(ValueError):
                release_section(markdown, "v3.3")

    def test_internal_separators_and_markdown_are_preserved(self):
        content = "**Fixes**\n\n- Use `<b>`\n\n---\n\nMore notes"
        self.assertEqual(release_section(f"### v3.0\n\n{content}\n\n---\n", "v3.0"), content)

    def test_english_first_chinese_collapsed_downloads_visible(self):
        assets = ["PaperTodo-v3.0-win-x64-self-contained-compressed.exe", "PaperTodo-v3.0-win-x64-no-runtime-uncompressed.exe", "SHA256SUMS.txt"]
        notes = render_notes("English **notes**", "中文 `原文`", "owner/repo", "v3.0", assets)
        self.assertTrue(notes.startswith("English **notes**\n\n<details>"))
        self.assertIn("\n\n中文 `原文`\n\n</details>", notes)
        downloads = notes.split("</details>")[1]
        for name in assets:
            self.assertIn(f"https://github.com/owner/repo/releases/download/v3.0/{name}", downloads)
        self.assertIn("runtime included", downloads)
        self.assertIn("Runtime required", downloads)
        self.assertNotIn("<details open", notes)

    def test_missing_translation_fails(self):
        for english, chinese in (("", "中文"), ("English", "  ")):
            with self.assertRaises(ValueError):
                render_notes(english, chinese, "owner/repo", "v3.0", ["app.exe"])

    def test_windows_7_remains_best_effort(self):
        notes = render_notes("English", "中文", "owner/repo", "v3.31", ["PaperTodo-v3.31-win7BestEffort-win-x64-self-contained.exe"])
        self.assertIn("Windows 7 SP1 x64, best effort", notes)


if __name__ == "__main__":
    unittest.main()
