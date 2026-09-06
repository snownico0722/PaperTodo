"""Behavior checks for the static website; no application build is needed.

Default: serve the real files over HTTP (also exercises asset paths and ?lang=).
--inline: render the exact local CSS/JS with set_content in restricted offline
browsers. This does NOT validate HTTP delivery, URL initialization or storage.
"""
from __future__ import annotations

import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import os
from threading import Thread
import unittest

from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2] / "website"
OPTIONS = None
BROWSER = None
BASE_URL = ""


def inline_html() -> str:
    html = (ROOT / "index.html").read_text(encoding="utf-8")
    html = html.replace('<link rel="stylesheet" href="assets/site.css">',
                        '<style>' + (ROOT / "assets/site.css").read_text(encoding="utf-8") + '</style>')
    html = html.replace('<script src="assets/site.js" defer></script>', '')
    return html.replace('</body>', '<script>' + (ROOT / "assets/site.js").read_text(encoding="utf-8") + '</script></body>')


class WebsiteTests(unittest.TestCase):
    def setUp(self):
        self.contexts = []
        self.errors = []
        self.failed_requests = []
        self.page = self.new_page()

    def tearDown(self):
        try:
            self.assertEqual(self.errors, [], "Uncaught JavaScript error")
            self.assertEqual(self.failed_requests, [], "Failed same-origin asset request")
        finally:
            for context in self.contexts:
                context.close()

    def new_page(self, width=1440, height=1000, *, touch=False, javascript=True, query="", blocked_storage=False):
        context = BROWSER.new_context(viewport={"width": width, "height": height},
                                      has_touch=touch, is_mobile=touch, java_script_enabled=javascript,
                                      reduced_motion="reduce")
        self.contexts.append(context)
        if blocked_storage:
            context.add_init_script("Object.defineProperty(window, 'localStorage', {get(){throw new Error('Storage blocked')}})")
        page = context.new_page()
        page.set_default_timeout(4000)
        page.on("pageerror", lambda error: self.errors.append(str(error)))
        if OPTIONS.inline:
            page.set_content(inline_html(), wait_until="load")
        else:
            page.on("response", lambda response: self.failed_requests.append(response.url)
                    if response.url.startswith(BASE_URL) and response.status >= 400 else None)
            page.goto(BASE_URL + query, wait_until="load")
        if javascript:
            expect(page.locator("html")).to_have_class("js")
        page.wait_for_timeout(50)  # Allow first ResizeObserver/layout frame.
        return page

    def assert_no_overflow(self, page):
        self.assertTrue(page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"))
        self.assertEqual(page.evaluate("""() => [...document.querySelectorAll('body *')].filter(el => {
            const r = el.getBoundingClientRect();
            return r.width > 0 && (r.left < -1 || r.right > innerWidth + 1)
                && getComputedStyle(el).position !== 'fixed';
        }).map(el => el.id || el.tagName).slice(0, 10)"""), [])

    def test_01_responsive_bilingual_layout(self):
        for width, height in [(320, 740), (390, 844), (640, 800), (641, 800),
                              (760, 900), (820, 1180), (1024, 768), (1280, 720),
                              (1440, 1000), (1920, 1080)]:
            with self.subTest(width=width):
                page = self.new_page(width, height, touch=width <= 640)
                for language in ["zh-CN", "en"]:
                    with self.subTest(language=language):
                        expect(page.locator("html")).to_have_attribute("lang", language)
                        self.assert_no_overflow(page)
                        expect(page.locator("#download")).to_be_visible()
                        if OPTIONS.screenshots and width in (390, 1440):
                            output = Path(OPTIONS.screenshots)
                            output.mkdir(parents=True, exist_ok=True)
                            page.screenshot(path=str(output / f"website-{width}-{language}.png"), full_page=True)
                    if language == "zh-CN":
                        page.locator("#lang-toggle").click()

    def test_02_todo_edit_add_check_undo(self):
        p = self.page
        edit = p.locator('[data-todo-edit="2"]')
        edit.fill("My own task 中文")
        p.locator('[data-todo-check="2"]').check()
        expect(p.locator("#todo-count")).to_have_text("2 / 3")
        p.locator("#undo-todo").click()
        expect(p.locator('[data-todo-check="2"]')).not_to_be_checked()
        expect(edit).to_have_value("My own task 中文")
        p.locator("#new-todo").fill("One more")
        p.locator("#add-form button").click()
        expect(p.locator('[data-todo-edit="4"]')).to_have_value("One more")
        p.locator("#undo-todo").click()
        expect(p.locator('[data-todo-edit="4"]')).to_have_count(0)
        expect(p.locator('[data-todo-edit]')).to_have_count(3)

    def test_03_enter_backspace_and_ime(self):
        p = self.page
        editor = p.locator('[data-todo-edit="2"]')
        editor.focus()
        editor.dispatch_event("keydown", {"key": "Enter", "isComposing": True, "keyCode": 229})
        expect(p.locator('[data-todo-edit]')).to_have_count(3)
        editor.press("Enter")
        expect(p.locator('[data-todo-edit="4"]')).to_be_focused()
        expect(p.locator('[data-todo-edit]')).to_have_count(4)
        p.locator('[data-todo-edit="4"]').press("Backspace")
        expect(p.locator('[data-todo-edit]')).to_have_count(3)
        expect(editor).to_be_focused()
        editor.press("Shift+Enter")
        expect(p.locator('[data-todo-edit]')).to_have_count(3)

    def test_04_preview_check_shares_paper_state(self):
        p = self.page
        p.locator('[data-todo-edit="2"]').fill("Shared preview text")
        p.locator("#fold-all").click()
        expect(p.locator("#paper-todo")).to_be_hidden()
        p.locator('[data-capsule="todo"]').hover()
        expect(p.locator("#edge-preview")).to_be_visible()
        expect(p.locator("#preview-body")).to_contain_text("Shared preview text")
        p.locator('[data-preview-check="2"]').check()
        expect(p.locator('[data-preview-check="2"]')).to_be_checked()
        p.locator("#preview-open").click()
        expect(p.locator('[data-todo-check="2"]')).to_be_checked()
        expect(p.locator("#paper-todo")).to_be_visible()
        expect(p.locator("#edge-preview")).to_be_hidden()

    def test_05_capsule_keyboard_dismiss_queue_side(self):
        p = self.page
        p.locator("#fold-all").click()
        capsule = p.locator('[data-capsule="note"]')
        capsule.focus()
        expect(p.locator("#edge-preview")).to_be_visible()
        capsule.press("Escape")
        expect(p.locator("#edge-preview")).to_be_hidden()
        expect(capsule).to_be_focused()
        capsule.press("Enter")
        expect(p.locator("#paper-note")).to_be_visible()
        p.locator("#fold-all").click()
        p.locator("#master-capsule").click()
        expect(p.locator("#dock-items")).to_be_hidden()
        p.locator("#master-capsule").click()
        expect(p.locator("#dock-items")).to_be_visible()
        p.locator("#swap-edge").click()
        expect(p.locator("#workspace")).to_have_attribute("data-side", "left")
        self.assert_no_overflow(p)

    def test_06_touch_preview_and_paper_switch(self):
        p = self.new_page(390, 844, touch=True)
        p.locator('[data-todo-edit="2"]').fill("Touch task")
        p.locator('[data-select-paper="note"]').tap()
        expect(p.locator("#paper-note")).to_be_visible()
        expect(p.locator("#paper-todo")).to_be_hidden()
        p.locator("#fold-all").tap()
        p.locator('[data-capsule="todo"]').tap()
        expect(p.locator("#edge-preview")).to_be_visible()
        p.locator('[data-preview-check="2"]').tap()
        p.locator("#preview-open").tap()
        expect(p.locator('[data-todo-edit="2"]')).to_have_value("Touch task")
        expect(p.locator('[data-todo-check="2"]')).to_be_checked()
        expect(p.locator("#edge-preview")).to_be_hidden()
        self.assert_no_overflow(p)

    def test_07_markdown_modes_safe_text_and_link(self):
        p = self.page
        p.locator('[data-open-note]').click()
        p.locator('[data-mode="raw"]').click()
        p.locator("#note-editor").fill('## Edited note\n\n**bold** and `code`\n\n<img src=x onerror="window.injected=1">\n<script>window.injected=1</script>')
        p.locator('[data-mode="raw"]').press("ArrowRight")
        expect(p.locator('[data-mode="basic"]')).to_be_focused()
        expect(p.locator("#note-basic")).to_contain_text("## Edited note")
        p.locator('[data-mode="basic"]').press("End")
        expect(p.locator("#panel-enhanced strong")).to_have_text("bold")
        expect(p.locator("#panel-enhanced")).to_contain_text("<img src=x")
        expect(p.locator("#panel-enhanced img, #panel-enhanced script")).to_have_count(0)
        self.assertIsNone(p.evaluate("window.injected"))
        p.locator('[data-fold="note"]').click()
        p.locator('[data-capsule="note"]').hover()
        expect(p.locator("#preview-body")).to_contain_text("Edited note")
        expect(p.locator("#preview-body img, #preview-body script")).to_have_count(0)

    def test_08_language_preserves_edits_theme_and_state(self):
        p = self.page
        p.locator('[data-todo-edit="2"]').fill("不翻译用户输入 / personal")
        p.locator('[data-mode="raw"]').click()
        p.locator("#note-editor").fill("## My note\nKeep me")
        p.locator('[data-palette="ink"]').click()
        p.locator("#lang-toggle").click()
        expect(p.locator('[data-todo-edit="2"]')).to_have_value("不翻译用户输入 / personal")
        expect(p.locator("#note-editor")).to_have_value("## My note\nKeep me")
        expect(p.locator("#playground")).to_have_attribute("data-theme", "ink")
        expect(p).to_have_title("PaperTodo — A few quiet, useful papers for Windows")
        expect(p.locator('meta[property="og:locale"]')).to_have_attribute("content", "en_US")
        expect(p.locator('[data-readme]').first).to_have_attribute("href", "https://github.com/snownico0722/PaperTodo/blob/main/README.en.md")
        p.locator("#lang-toggle").click()
        expect(p.locator("html")).to_have_attribute("lang", "zh-CN")
        expect(p.locator('[data-todo-edit="2"]')).to_have_value("不翻译用户输入 / personal")

    def test_09_resize_keeps_data_and_papers_in_bounds(self):
        p = self.page
        p.locator('[data-todo-edit="2"]').fill("Resize does not reset")
        for width in [820, 390, 1280, 641, 320, 1920]:
            p.set_viewport_size({"width": width, "height": 900})
            p.wait_for_timeout(60)
            self.assert_no_overflow(p)
            if width > 640:
                self.assertTrue(p.evaluate("""() => {
                    const r = document.querySelector('#workspace').getBoundingClientRect();
                    return [...document.querySelectorAll('.paper:not([hidden])')].every(el => {
                        const p = el.getBoundingClientRect();
                        return p.left >= r.left && p.right <= r.right && p.top >= r.top && p.bottom <= r.bottom;
                    });
                }"""))
        expect(p.locator('[data-todo-edit="2"]')).to_have_value("Resize does not reset")

    def test_10_drag_keyboard_cancel_and_edge_drop(self):
        p = self.page
        handle = p.locator('[data-drag="todo"]')
        handle.focus()
        before = p.locator("#paper-todo").bounding_box()
        handle.press("ArrowRight")
        after = p.locator("#paper-todo").bounding_box()
        self.assertAlmostEqual(after["x"] - before["x"], 8, delta=1)
        rect = handle.bounding_box()
        x, y = rect["x"] + 50, rect["y"] + 20
        p.mouse.move(x, y)
        p.mouse.down()
        p.mouse.move(x + 40, y + 40, steps=5)
        p.keyboard.press("Escape")
        p.mouse.up()
        final = p.locator("#paper-todo").bounding_box()
        self.assertAlmostEqual(final["x"], after["x"], delta=1)
        self.assertAlmostEqual(final["y"], after["y"], delta=1)
        rect = handle.bounding_box()
        stage = p.locator("#workspace").bounding_box()
        p.mouse.move(rect["x"] + 50, rect["y"] + 20)
        p.mouse.down()
        p.mouse.move(stage["x"] + 3, rect["y"] + 20, steps=8)
        expect(p.locator("#drop-indicator")).to_be_visible()
        p.mouse.up()
        expect(p.locator("#paper-todo")).to_be_hidden()
        expect(p.locator('[data-capsule="todo"]')).to_be_visible()
        expect(p.locator("#workspace")).to_have_attribute("data-side", "left")

    def test_11_focus_timer_pause_fold_resume_reset(self):
        p = self.page
        p.clock.install()
        p.locator("#timer-toggle").click()
        p.clock.fast_forward(3000)
        self.assertNotEqual(p.locator("#timer-output").inner_text(), "25:00")
        p.locator("#timer-toggle").click()
        paused = p.locator("#timer-output").inner_text()
        p.clock.fast_forward(5000)
        expect(p.locator("#timer-output")).to_have_text(paused)
        p.locator("#focus-fold").click()
        expect(p.locator("#focus-body")).to_be_hidden()
        expect(p.locator("#mini-time")).to_have_text(paused)
        p.locator("#focus-mini").click()
        p.locator("#timer-toggle").click()
        p.clock.fast_forward(3000)
        self.assertNotEqual(p.locator("#timer-output").inner_text(), paused)
        p.locator("#timer-reset").click()
        expect(p.locator("#timer-output")).to_have_text("25:00")
        p.clock.fast_forward(10000)
        expect(p.locator("#timer-output")).to_have_text("25:00")
        p.locator("#timer-toggle").click()
        p.clock.fast_forward(1500001)
        expect(p.locator("#timer-output")).to_have_text("00:00")
        expect(p.locator("#timer-status")).to_contain_text("完成")

    def test_12_script_simulates_without_network_or_popup(self):
        p = self.page
        requests = []
        p.on("request", lambda request: requests.append(request.url))
        count = len(p.context.pages)
        p.locator("#run-script").click()
        expect(p.locator("#script-result")).to_be_visible()
        expect(p.locator("#browser-address")).to_have_text("https://example.com")
        expect(p.locator("#script-status")).to_contain_text("没有执行")
        self.assertEqual(requests, [])
        self.assertEqual(len(p.context.pages), count)
        p.locator("#run-script").click()
        expect(p.locator("#script-result")).to_be_visible()

    def test_13_mobile_menu_and_native_faq(self):
        p = self.new_page(390, 844, touch=True)
        toggle = p.locator("#menu-toggle")
        toggle.tap()
        expect(toggle).to_have_attribute("aria-expanded", "true")
        p.keyboard.press("Escape")
        expect(toggle).to_have_attribute("aria-expanded", "false")
        expect(toggle).to_be_focused()
        toggle.tap()
        p.locator('#site-menu a[href="#faq"]').tap()
        expect(toggle).to_have_attribute("aria-expanded", "false")
        summary = p.locator("#faq details summary").first
        summary.tap()
        expect(p.locator("#faq details").first).to_have_attribute("open", "")
        self.assert_no_overflow(p)

    def test_14_native_scroll_and_reduced_motion(self):
        p = self.page
        p.evaluate("scrollTo(0,0)")
        p.mouse.move(30, 200)
        p.mouse.wheel(0, 130)
        p.wait_for_timeout(200)
        y = p.evaluate("scrollY")
        self.assertGreater(y, 70)
        self.assertLess(y, 210)
        self.assertEqual(p.evaluate("getComputedStyle(document.documentElement).scrollSnapType"), "none")
        self.assertEqual(p.evaluate("getComputedStyle(document.documentElement).scrollBehavior"), "auto")

    def test_15_links_labels_and_unique_ids(self):
        p = self.page
        self.assertEqual(p.evaluate("""() => [...document.querySelectorAll('a[href^="#"]')]
            .filter(a => !document.getElementById(a.hash.slice(1))).map(a => a.hash)"""), [])
        self.assertEqual(p.evaluate("""() => [...document.querySelectorAll('a[target="_blank"]')]
            .filter(a => !a.rel.includes('noopener') && !a.rel.includes('noreferrer')).map(a => a.href)"""), [])
        self.assertEqual(p.evaluate("""() => {
            const ids = [...document.querySelectorAll('[id]')].map(el => el.id);
            return ids.filter((id,index) => ids.indexOf(id) !== index);
        }"""), [])
        self.assertEqual(p.evaluate("""() => [...document.querySelectorAll('button')].filter(el =>
            !el.getAttribute('aria-label') && !el.textContent.trim()).map(el => el.outerHTML)"""), [])

    def test_16_no_javascript_content_remains_readable(self):
        p = self.new_page(390, 844, touch=True, javascript=False)
        expect(p.locator("h1")).to_be_visible()
        expect(p.locator('#download a[href$="/releases/latest"]')).to_be_visible()
        expect(p.locator("#faq details summary").first).to_be_visible()
        p.locator("#faq details summary").first.click()
        expect(p.locator("#faq details").first).to_have_attribute("open", "")
        self.assert_no_overflow(p)

    def test_17_reset_clears_only_the_main_demo(self):
        p = self.page
        p.locator('[data-todo-edit="2"]').fill("Temporary")
        p.locator('[data-palette="forest"]').click()
        p.locator("#fold-all").click()
        p.locator("#reset-demo").click()
        expect(p.locator("#edge-dock")).to_be_hidden()
        expect(p.locator("#playground")).to_have_attribute("data-theme", "paper")
        expect(p.locator('[data-todo-edit="2"]')).not_to_have_value("Temporary")
        expect(p.locator("#undo-todo")).to_be_disabled()

    def test_18_url_language_and_blocked_storage(self):
        if OPTIONS.inline:
            self.skipTest("HTTP-only: inline mode has no navigable origin or query string")
        p = self.new_page(query="?lang=en&keep=1#faq", blocked_storage=True)
        expect(p.locator("html")).to_have_attribute("lang", "en")
        p.locator("#lang-toggle").click()
        expect(p.locator("html")).to_have_attribute("lang", "zh-CN")
        self.assertIn("lang=zh", p.url)
        self.assertIn("keep=1", p.url)
        self.assertTrue(p.url.endswith("#faq"))
        p.reload()
        expect(p.locator("html")).to_have_attribute("lang", "zh-CN")


class QuietHandler(SimpleHTTPRequestHandler):
    def log_message(self, *_args):
        pass


def main():
    global OPTIONS, BROWSER, BASE_URL
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inline", action="store_true")
    parser.add_argument("--browser", default=os.environ.get("CHROMIUM_PATH"))
    parser.add_argument("--screenshots", help="Optional directory for full-page screenshots")
    OPTIONS = parser.parse_args()
    server = None
    if not OPTIONS.inline:
        server = ThreadingHTTPServer(("127.0.0.1", 0), partial(QuietHandler, directory=str(ROOT)))
        Thread(target=server.serve_forever, daemon=True).start()
        BASE_URL = f"http://127.0.0.1:{server.server_port}/"
    try:
        with sync_playwright() as playwright:
            kwargs = {"executable_path": OPTIONS.browser} if OPTIONS.browser else {}
            BROWSER = playwright.chromium.launch(headless=True, **kwargs)
            suite = unittest.defaultTestLoader.loadTestsFromTestCase(WebsiteTests)
            result = unittest.TextTestRunner(verbosity=2).run(suite)
            BROWSER.close()
            return 0 if result.wasSuccessful() else 1
    finally:
        if server:
            server.shutdown()
            server.server_close()


if __name__ == "__main__":
    raise SystemExit(main())
