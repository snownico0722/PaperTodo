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
let onEvent, initialize, globalActions, inspectedPaper;
const context = {
  console: { log() {}, error: console.error },
  window: { addEventListener(name, handler) { if (name === 'papertodo') initialize = handler; } },
  papertodo: {
    onEvent(handler) { onEvent = handler; },
    papers: {
      async setHeaderText(id, text) { headers.set(id, text); },
      async setCapsulePresentation() {}
    },
    globalTopBar: { async setActions(actions) { globalActions = actions; } },
    workspace: { async request(method, params) { inspectedPaper = params.paperId; return {}; } }
  }
};
vm.runInNewContext(script, context, { filename: 'TopBarWeb/runtime.html' });
(async () => {
  initialize({ detail: { type: 'initialize', papers: [{ paperId: 'p1' }] } });
  assert.equal(manifest.apiVersion, '2.3');
  assert.equal(globalActions[0].menuOnly, true);
  await onEvent({ type: 'topBarActionInvoked', action: { actionId: 'inspect-current', targetPaperId: 'target' } });
  assert.equal(inspectedPaper, 'target');
  await onEvent({ type: 'shortcutInvoked', settingId: 'runtimePingAction', actionId: 'runtime.ping' });
  assert.equal(headers.get('p1'), 'Top Bar 2.3 · 1');
  await onEvent({ type: 'shortcutInvoked', settingId: 'runtimePingShortcut', actionId: 'runtime.ping' });
  assert.equal(headers.get('p1'), 'Top Bar 2.3 · 2');
  await onEvent({ type: 'shortcutInvoked', settingId: 'unknown', actionId: 'other.action' });
  assert.equal(headers.get('p1'), 'Top Bar 2.3 · 2');
  console.log('Web settings and menu-only action sample: passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
