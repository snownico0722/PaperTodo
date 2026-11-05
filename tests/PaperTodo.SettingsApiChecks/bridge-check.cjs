'use strict';
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
(async () => {
  const posted = [], listeners = [];
  const context = {
    location: {origin: 'https://settings.test'},
    document: {readyState: 'loading', addEventListener() {}, querySelectorAll() { return []; }, documentElement: {}},
    addEventListener() {}, dispatchEvent() {}, CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options?.detail; } },
    chrome: {webview: {postMessage(message) { posted.push(message); }, addEventListener(_, listener) { listeners.push(listener); }}},
    requestAnimationFrame() { return 1; }, console
  };
  context.window = context; context.top = context;
  const script = fs.readFileSync(process.argv[2], 'utf8');
  vm.runInNewContext(script, context);
  const deliver = data => listeners.forEach(listener => listener({data}));
  const api = context.papertodo.settingsApi;
  assert.ok(Object.isFrozen(api));
  const initial = api.get('todo.paper_links');
  await Promise.resolve(); assert.equal(posted.filter(x => x.type === 'hostRequest').length, 0);
  deliver({type: 'initialize', documentToken: 'document', uiToken: 'document'});
  await new Promise(resolve => setImmediate(resolve));
  let request = posted.find(x => x.type === 'hostRequest'); posted.length = 0;
  assert.equal(request.payload.method, 'appSettings.get'); assert.equal(request.payload.params.id, 'todo.paper_links');
  if (process.argv[3] === 'runtime') assert.equal(request.payload.target, 'workspace');
  deliver({type: 'hostResponse', requestId: request.payload.requestId, ok: true, result: {id: 'todo.paper_links', value: false}});
  assert.equal((await initial).value, false);
  const result = api.set('todo.paper_links', true);
  await new Promise(resolve => setImmediate(resolve));
  request = posted.find(x => x.type === 'hostRequest'); posted.length = 0;
  assert.equal(request.payload.method, 'appSettings.set'); assert.equal(request.payload.params.value, true);
  deliver({type: 'hostResponse', requestId: request.payload.requestId, ok: false, error: {code: 'permission_denied', message: 'denied'}});
  await assert.rejects(result, error => error.code === 'permission_denied');
  const list = api.list('appearance');
  await new Promise(resolve => setImmediate(resolve));
  request = posted.find(x => x.type === 'hostRequest');
  assert.equal(request.payload.method, 'appSettings.list'); assert.equal(request.payload.params.category, 'appearance');
  deliver({type: 'hostResponse', requestId: request.payload.requestId, ok: true, result: []});
  assert.equal((await list).length, 0);
  const foreign = {...context, location: {origin: 'https://untrusted.test'}};
  delete foreign.papertodo; foreign.window = foreign; foreign.top = foreign;
  vm.runInNewContext(script, foreign);
  assert.equal(foreign.papertodo, undefined);
  console.log(`${process.argv[3]} settings bridge behavior passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
