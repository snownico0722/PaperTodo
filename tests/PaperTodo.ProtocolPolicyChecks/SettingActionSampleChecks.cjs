 'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const sample = path.resolve(__dirname, '../../plugin-samples/PaperTodo.Plugin.TopBarWeb');
const manifest = JSON.parse(fs.readFileSync(path.join(sample, 'plugin.json'), 'utf8'));
const button = manifest.settings.find(s => s.id === 'runtimePingAction');
assert.equal(button.type, 'action');
assert.equal(button.action, 'runtime.ping');
assert.equal(button.default, undefined);
assert.ok(manifest.capabilities.includes('runtime'));
const script = fs.readFileSync(path.join(sample, 'web/runtime.html'), 'utf8').match(/<script>([\s\S]*?)<\/script>/)[1];
const headers = new Map();
let onEvent, initialize;
const context = {
  console: { log() {}, error: console.error },
  window: { addEventListener(name, handler) { if (name === 'papertodo') initialize = handler; } },
  papertodo: {
    onEvent(handler) { onEvent = handler; },
    papers: {
      async setHeaderText(id, text) { headers.set(id, text); },
      async setCapsulePresentation() {}
    },
    globalTopBar: { async setActions() {} },
    workspace: { async request() { return {}; } }
  }
};
vm.runInNewContext(script, context, { filename: 'TopBarWeb/runtime.html' });
(async () => {
  initialize({ detail: { type: 'initialize', papers: [{ paperId: 'p1' }] } });
  await onEvent({ type: 'shortcutInvoked', settingId: 'runtimePingAction', actionId: 'runtime.ping' });
  assert.equal(headers.get('p1'), 'Top Bar 2.1 · 1');
  await onEvent({ type: 'shortcutInvoked', settingId: 'runtimePingShortcut', actionId: 'runtime.ping' });
  assert.equal(headers.get('p1'), 'Top Bar 2.1 · 2');
  await onEvent({ type: 'shortcutInvoked', settingId: 'unknown', actionId: 'other.action' });
  assert.equal(headers.get('p1'), 'Top Bar 2.1 · 2');
  console.log('Web settings action sample: 7 behavior checks passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
