"""Behavioral Chromium tests against an API 2.1 mock, NOT Windows/WebView2 integration tests.

Default mode serves the unchanged production files (including CSP) over localhost.
--inline is for runners whose managed Chromium prohibits navigation: the same JS/CSS
is injected into about:blank; CSP/resource loading are NOT covered in that mode.
"""
from __future__ import annotations
import argparse
import base64
import functools
import http.server
import json
import os
from pathlib import Path
import re
import threading
import unittest
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parent.parent
ARGS = argparse.Namespace(inline=False, screenshots=None)
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN3cAAAAASUVORK5CYII=')


def blank(**kwargs):
    return dict(schema=1, sources=[], notes=[], links=[], positions={}, covers={}, view='map', filter='all', wiki=True, **{}) | kwargs


def board(**kwargs):
    return blank(sources=['p1'], notes=[dict(id='n:idea', title='Idea', body='A local thought.')]) | kwargs


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *_):
        pass


class BrowserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(QuietHandler, directory=str(ROOT / 'web')))
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.play = sync_playwright().start()
        opts = dict(headless=True)
        if executable := os.getenv('CHROMIUM_EXECUTABLE'):
            opts['executable_path'] = executable
        cls.browser = cls.play.chromium.launch(**opts)

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.play.stop()
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        self.page = self.browser.new_page(viewport=dict(width=1100, height=760))
        self.page.set_default_timeout(5000)
        self.errors = []
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))

    def tearDown(self):
        self.page.close()
        self.assertEqual(self.errors, [], 'Uncaught JavaScript errors')

    def load(self, state=None, surface='body', preview=False, no_host=False):
        name = 'preview.html' if preview else 'index.html'
        mock = (ROOT / 'tests/host-fixture.js').read_text(encoding='utf-8')
        if ARGS.inline:
            html = (ROOT / 'web' / name).read_text(encoding='utf-8')
            html = re.sub(r'<meta http-equiv="Content-Security-Policy"[^>]+>', '', html)
            html = re.sub(r'<link rel="stylesheet" href="([^"]+)">', lambda m: '<style>' + (ROOT / 'web' / m[1]).read_text(encoding='utf-8') + '</style>', html)
            html = re.sub(r'<script[^>]*src="[^"]+"[^>]*></script>', '', html)
            self.page.set_content(html)
            # about:blank has no secure-origin randomUUID; the production HTTPS host does.
            self.page.evaluate('crypto.randomUUID ||= () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b=>b.toString(16).padStart(2,"0")).join("")')
            if not preview and not no_host:
                self.page.evaluate(mock)
            for script in ['core.js', 'i18n.js', 'art.js', 'app.js']:
                self.page.add_script_tag(content=(ROOT / 'web' / script).read_text(encoding='utf-8'))
        else:
            if not preview and not no_host:
                self.page.add_init_script(mock)
            self.page.goto(f'http://127.0.0.1:{self.server.server_port}/{name}')
        if not preview and not no_host:
            self.page.evaluate('([state,surface]) => testHost.initialize(state,surface)', [state, surface])
        self.page.wait_for_timeout(100)

    def js(self, expression):
        return self.page.evaluate(expression)

    def saved(self):
        return self.js('testHost.saves.at(-1)')

    def click_node(self, key):
        self.page.locator(f'[data-node="{key}"]').click()

    def test_01_no_host_never_runs_fake_data(self):
        self.load(no_host=True)
        expect(self.page.locator('#app')).to_contain_text('PaperTodo')
        self.assertEqual(self.page.locator('[data-node]').count(), 0)

    def test_02_blank_does_not_scan_contents_or_write_defaults(self):
        self.load()
        self.assertEqual(self.js('testHost.saves'), [])
        self.assertEqual([x['method'] for x in self.js('testHost.calls')], ['papers.list'])
        self.assertEqual(self.js('testHost.providers.length'), 1)
        self.assertEqual(self.page.locator('[data-node]').count(), 0)

    def test_03_sources_are_explicit_and_exclude_other_plugin_bodies(self):
        self.load()
        self.page.locator('#sources').click()
        expect(self.page.locator('#dialog')).not_to_contain_text('Starpaper')
        self.page.get_by_label('Project', exact=True).check()
        self.page.get_by_label('Reference', exact=True).check()
        self.page.locator('#dialog button[type=submit]').click()
        expect(self.page.locator('[data-node="t:p1:t1"]')).to_be_visible()
        self.assertEqual(self.saved()['sources'], ['p1', 'p3'])
        self.assertFalse(any(c['args'].get('paperId') == 'p2' for c in self.js('testHost.calls')))
        self.click_node('p:p3')
        expect(self.page.locator('#inspector')).to_contain_text('Live reference text.')
        self.assertEqual(self.page.locator('#inspector').get_by_role('button', name='Edit', exact=True).count(), 0)

    def test_04_new_idea_edit_delete_and_local_undo(self):
        self.load()
        self.page.locator('#add-note').click()
        self.page.get_by_label('Title', exact=True).fill('First idea')
        self.page.get_by_label('Text · supports [[Idea title]]', exact=True).fill('Preserve this text.')
        self.page.locator('#dialog button[type=submit]').click()
        expect(self.page.locator('#inspector h2')).to_have_text('First idea')
        self.assertEqual(len(self.saved()['notes']), 1)
        self.page.locator('#inspector').get_by_role('button', name='Edit', exact=True).click()
        self.page.get_by_label('Title', exact=True).fill('Edited idea')
        self.page.locator('#dialog button[type=submit]').click()
        self.page.locator('#undo').click()
        self.assertEqual(self.saved()['notes'][0]['title'], 'First idea')
        self.click_node(self.saved()['notes'][0]['id'])
        self.page.get_by_role('button', name='Delete idea', exact=True).click()
        self.page.locator('#dialog button[type=submit]').click()
        self.assertEqual(self.saved()['notes'], [])
        self.page.locator('#undo').click()
        self.assertEqual(len(self.saved()['notes']), 1)
        self.assertTrue(all(c['method'] == 'papers.list' for c in self.js('testHost.calls')))

    def test_05_drag_is_one_save_on_release_and_cancel_has_no_save(self):
        self.load(board(sources=[]))
        node = self.page.locator('[data-node="n:idea"]')
        bounds = node.evaluate('(el) => el.getBoundingClientRect().toJSON()')
        x, y = bounds['x'] + bounds['width'] / 2, bounds['y'] + 20
        self.page.mouse.move(x, y)
        self.page.mouse.down()
        self.page.mouse.move(x + 100, y + 35, steps=8)
        self.assertEqual(self.js('testHost.saves.length'), 0)
        self.page.mouse.up()
        self.assertEqual(self.js('testHost.saves.length'), 1)
        self.assertIn('n:idea', self.saved()['positions'])
        self.page.wait_for_timeout(120)
        self.page.locator('#undo').click()
        self.assertEqual(self.saved()['positions'], {})
        before = self.js('testHost.saves.length')
        expect(node).to_be_visible()
        bounds = node.evaluate('(el) => el.getBoundingClientRect().toJSON()')
        self.page.mouse.move(bounds['x'] + bounds['width'] / 2, bounds['y'] + 20)
        self.page.mouse.down()
        self.page.mouse.move(bounds['x'] + 140, bounds['y'] + 70, steps=5)
        self.js('testHost.emit({type:"cancelInteractions"})')
        self.page.mouse.up()
        self.assertEqual(self.js('testHost.saves.length'), before)

    def test_06_shift_click_links_and_named_link_update(self):
        self.load(board(sources=[], notes=[dict(id='n:idea',title='Idea',body=''),dict(id='n:two',title='Second',body='')]))
        self.click_node('n:idea')
        self.page.locator('[data-node="n:two"]').click(modifiers=['Shift'])
        self.assertEqual(len(self.saved()['links']), 1)
        self.page.get_by_role('button', name='Link', exact=True).click()
        self.page.get_by_label('Relationship (optional)', exact=True).fill('Enables')
        self.page.locator('#dialog button[type=submit]').click()
        self.assertEqual(self.saved()['links'][0]['label'], 'Enables')
        self.assertEqual(len(self.saved()['links']), 1)

    def test_07_task_write_waits_for_ack_and_uses_exact_ids(self):
        self.load(board(view='cards'))
        self.js('testHost.delayWrite=true')
        check = self.page.get_by_role('checkbox', name='Mark done: Read a book', exact=True)
        check.click()
        expect(check).to_be_disabled()
        self.assertEqual(self.js('testHost.todos[0].done'), False)
        writes = self.js('testHost.calls.filter(c=>c.method==="todos.update")')
        self.assertEqual(writes, [dict(method='todos.update',args=dict(paperId='p1',todoId='t1',done=True))])
        self.js('testHost.resolveWrite()')
        expect(self.page.get_by_role('checkbox', name='Mark open: Read a book', exact=True)).to_be_enabled()
        self.assertEqual(self.js('testHost.saves'), [])

    def test_08_failed_task_write_is_not_automatically_retried(self):
        self.load(board(view='cards'))
        self.js('testHost.failWrite=true')
        self.page.get_by_role('checkbox', name='Mark done: Read a book', exact=True).click()
        expect(self.page.locator('#toast')).to_contain_text('Operation not confirmed')
        self.page.wait_for_timeout(150)
        self.assertEqual(self.js('testHost.calls.filter(c=>c.method==="todos.update").length'), 1)
        self.assertFalse(self.js('testHost.todos[0].done'))

    def test_09_read_failure_preserves_snapshot_and_disables_host_edits(self):
        self.load(board(view='cards'))
        self.js('testHost.failRead=true; testHost.change()')
        expect(self.page.locator('#status-banner')).to_be_visible()
        expect(self.page.locator('#card-grid')).to_contain_text('Read a book')
        expect(self.page.get_by_role('checkbox', name='Mark done: Read a book', exact=True)).to_be_disabled()
        self.js('testHost.failRead=false; testHost.todos[0].text="Changed elsewhere"; testHost.change()')
        expect(self.page.locator('#card-grid')).to_contain_text('Changed elsewhere')
        expect(self.page.locator('#status-banner')).to_be_hidden()
        self.assertEqual(self.js('testHost.saves'), [])

    def test_10_state_changed_never_echoes_and_future_state_blocks_writes(self):
        self.load(board())
        next_state = board(notes=[dict(id='n:next',title='From host',body='')])
        self.page.evaluate('(state)=>testHost.emit({type:"stateChanged",state})', next_state)
        expect(self.page.locator('[data-node="n:next"]')).to_be_visible()
        self.assertEqual(self.js('testHost.saves'), [])
        self.js('testHost.emit({type:"stateChanged",state:{schema:99,original:"keep"}})')
        expect(self.page.locator('#app')).to_contain_text('Incompatible data version')
        self.assertEqual(self.js('testHost.saves'), [])
        self.assertEqual(self.js('(()=>{try{testHost.providers[0]();return "bad"}catch{return "blocked"}})()'), 'blocked')

    def test_11_invalid_initial_state_has_no_state_provider_or_read(self):
        self.load(dict(schema=99, original='do not overwrite'))
        expect(self.page.locator('#app')).to_contain_text('Incompatible data version')
        self.assertEqual(self.js('testHost.providers'), [])
        self.assertEqual(self.js('testHost.calls'), [])
        self.assertEqual(self.js('testHost.saves'), [])

    def test_12_image_upload_template_reset_and_png_export(self):
        self.load(board(sources=[]))
        self.click_node('n:idea')
        self.page.get_by_role('button', name='Use local image', exact=True).click()
        self.page.locator('#image-file').set_input_files(dict(name='test.png',mimeType='image/png',buffer=PNG))
        self.page.wait_for_function('testHost.saves.at(-1)?.covers["n:idea"]?.image')
        self.assertTrue(self.saved()['covers']['n:idea']['image'].startswith('data:image/webp;base64,'))
        self.page.get_by_role('combobox', name='Illustration', exact=True).select_option('travel')
        self.assertEqual(self.saved()['covers']['n:idea'], dict(scene='travel'))
        with self.page.expect_download() as result:
            self.page.get_by_role('button', name='Export illustrated card', exact=True).click()
        download = result.value
        self.assertEqual(download.suggested_filename, 'Starpaper-card.png')
        raw = Path(download.path()).read_bytes()
        self.assertEqual(raw[:8], b'\x89PNG\r\n\x1a\n')
        self.assertEqual(int.from_bytes(raw[16:20], 'big'), 960)
        self.assertEqual(int.from_bytes(raw[20:24], 'big'), 1120)

    def test_13_bad_image_and_bad_backup_do_not_overwrite(self):
        self.load(board(sources=[]))
        self.click_node('n:idea')
        self.page.get_by_role('button', name='Use local image', exact=True).click()
        self.page.locator('#image-file').set_input_files(dict(name='bad.svg',mimeType='image/svg+xml',buffer=b'<svg onload="alert(1)"/>'))
        expect(self.page.locator('#toast')).to_contain_text('Unsupported image')
        self.assertEqual(self.js('testHost.saves'), [])
        self.page.locator('#backup-file').set_input_files(dict(name='bad.json',mimeType='application/json',buffer=b'{"format":"papertodo.starpaper","version":1,"state":{"schema":55}}'))
        expect(self.page.locator('#toast')).to_contain_text('Incompatible data version')
        self.assertEqual(self.js('testHost.saves'), [])

    def test_14_mini_has_layout_ready_but_no_writers_or_interactive_controls(self):
        self.load(board(), surface='mini')
        self.page.wait_for_function('testHost.readyCount === 1')
        expect(self.page.locator('#app')).to_contain_text('Read a book')
        self.assertEqual(self.js('testHost.providers'), [])
        self.assertEqual(self.js('testHost.saves'), [])
        self.assertEqual(self.js('testHost.capsules'), [])
        self.assertEqual(self.js('testHost.claims'), [])
        self.assertEqual(self.page.locator('#app button, #app input, [data-papertodo-interactive]').count(), 0)
        self.assertGreater(self.js('testHost.readyBox.height'), 0)
        self.page.evaluate('(state)=>testHost.emit({type:"stateChanged",state})', board(notes=[]))
        self.page.wait_for_timeout(50)
        self.assertEqual(self.js('testHost.readyCount'), 1)
        self.assertEqual(self.js('testHost.saves'), [])

    def test_15_theme_languages_and_narrow_viewport(self):
        self.load(board())
        for language in ['zh', 'en', 'ja', 'ko']:
            self.page.evaluate('(language)=>testHost.emit({type:"settingsChanged",settings:{language}})', language)
            self.assertEqual(self.page.locator('html').get_attribute('lang'), language)
        self.js('testHost.emit({type:"themeChanged",theme:{paperColor:"#202825",textColor:"#edf2ef",weakTextColor:"#b1bfb5",accentColor:"#a5c8b5",borderColor:"#60726a"}})')
        self.assertEqual(self.js('getComputedStyle(document.documentElement).getPropertyValue("--paper").trim()'), '#202825')
        self.page.set_viewport_size(dict(width=320,height=450))
        self.page.locator('#tab-cards').click()
        self.assertLessEqual(self.js('document.documentElement.scrollWidth'), 320)
        self.assertGreater(self.page.locator('#cards-pane').bounding_box()['height'], 80)
        self.page.locator('#add-note').click()
        bounds = self.page.locator('#dialog').bounding_box()
        self.assertGreaterEqual(bounds['x'], 0)
        self.assertLessEqual(bounds['x']+bounds['width'], 321)
        self.assertLessEqual(bounds['y']+bounds['height'], 451)

    def test_16_export_backup_svg_and_import_are_local(self):
        self.load(board())
        self.page.locator('#menu summary').click()
        with self.page.expect_download() as result:
            self.page.locator('#export').click()
        data = Path(result.value.path()).read_bytes()
        parsed = json.loads(data)
        self.assertEqual(parsed['state'], board())
        self.assertNotIn(b'Read a book', data)
        self.page.locator('#menu summary').click()
        with self.page.expect_download() as result:
            self.page.locator('#export-svg').click()
        self.assertIn(b'<svg', Path(result.value.path()).read_bytes())
        replacement = dict(format='papertodo.starpaper',version=1,state=blank(notes=[dict(id='n:new',title='Imported',body='Safe text')]))
        self.page.locator('#backup-file').set_input_files(dict(name='backup.json',mimeType='application/json',buffer=json.dumps(replacement).encode()))
        self.page.locator('#dialog button[type=submit]').click()
        self.assertEqual(self.saved()['notes'][0]['title'], 'Imported')
        self.page.locator('#undo').click()
        self.assertEqual(self.saved()['notes'][0]['title'], 'Idea')
        self.assertFalse(any(c['method'] in ['todos.update','todos.append','notes.write'] for c in self.js('testHost.calls')))

    def test_17_create_task_and_conflicting_edit(self):
        self.load(board(view='cards'))
        self.page.locator('#add-todo').click()
        self.page.get_by_label('Title', exact=True).fill('A new task')
        self.page.locator('#dialog button[type=submit]').click()
        expect(self.page.locator('#card-grid')).to_contain_text('A new task')
        self.assertEqual(self.js('testHost.calls.filter(c=>c.method==="todos.append").length'), 1)
        self.page.locator('[data-card="t:p1:t1"] .card-art').click()
        self.page.locator('#inspector').get_by_role('button', name='Edit', exact=True).click()
        self.page.get_by_label('Title', exact=True).fill('Overwrite attempt')
        self.js('testHost.todos[0].text="Concurrent change"')
        self.page.locator('#dialog button[type=submit]').click()
        expect(self.page.locator('#toast')).to_contain_text('Content changed elsewhere')
        self.assertEqual(self.js('testHost.calls.filter(c=>c.method==="todos.update").length'), 0)

    def test_18_save_failure_keeps_state_and_input(self):
        self.load(board(sources=[]))
        self.js('testHost.failSave=true')
        self.page.locator('#add-note').click()
        self.page.get_by_label('Title', exact=True).fill('Do not lose me')
        self.page.locator('#dialog button[type=submit]').click()
        expect(self.page.locator('#dialog')).to_be_visible()
        expect(self.page.get_by_label('Title', exact=True)).to_have_value('Do not lose me')
        self.assertEqual(self.js('testHost.providers[0]().notes.length'), 1)
        self.assertEqual(self.js('testHost.saves'), [])

    def test_20_keyboard_task_checkbox_is_not_intercepted_by_map_shortcuts(self):
        self.load(board(view='cards'))
        check = self.page.get_by_role('checkbox', name='Mark done: Read a book', exact=True)
        check.focus()
        self.page.keyboard.press('Space')
        updated = self.page.get_by_role('checkbox', name='Mark open: Read a book', exact=True)
        expect(updated).to_be_enabled()
        expect(updated).to_be_focused()
        self.assertEqual(self.js('testHost.calls.filter(c=>c.method==="todos.update").length'), 1)
        self.assertEqual(self.js('testHost.saves'), [])

    def test_19_explicit_preview_and_screenshots(self):
        self.load(preview=True)
        expect(self.page.locator('#demo-banner')).to_be_visible()
        self.assertEqual(self.page.locator('[data-node]').count(), 11)
        if ARGS.screenshots:
            out = Path(ARGS.screenshots); out.mkdir(parents=True,exist_ok=True)
            self.page.screenshot(path=str(out/'Starpaper-map.png'))
            self.page.locator('#tab-cards').click()
            self.page.screenshot(path=str(out/'Starpaper-cards.png'))
            self.page.locator('[data-card="t:preview-tasks:reading"] .card-art').click()
            self.page.screenshot(path=str(out/'Starpaper-inspector.png'))
            self.page.set_viewport_size(dict(width=320,height=450))
            self.page.screenshot(path=str(out/'Starpaper-narrow.png'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inline',action='store_true')
    parser.add_argument('--screenshots')
    ARGS, remaining = parser.parse_known_args()
    unittest.main(argv=[__file__]+remaining,verbosity=2)
