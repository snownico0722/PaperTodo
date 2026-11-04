const vm = require('node:vm'), fs = require('node:fs'), assert = require('node:assert/strict');
(async () => {
  const posted = [], listeners = [], received = [], errors = [];
  const scope = {
    location: { origin: 'https://reminder.test' },
    chrome: { webview: { postMessage: value => posted.push(value), addEventListener: (_, fn) => listeners.push(fn) } },
    dispatchEvent() {}, CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init?.detail; } }
  };
  scope.window = scope; scope.top = scope;
  vm.runInNewContext(fs.readFileSync(process.argv[2], 'utf8'), scope);
  const api = scope.papertodo, deliver = data => listeners.forEach(fn => fn({ data }));
  api.onEvent(message => { if (message.type === 'hostSubscriptionError') errors.push(message); });
  const stop = api.onHostEvent(['todo.changed'], value => received.push(value), { paperIds: ['p'] });
  assert.equal(posted.length, 0);
  deliver({ type: 'initialize', uiToken: 'current' });
  const subscribe = posted.shift();
  assert.equal(subscribe.payload.method, 'events.subscribe');
  assert.equal(subscribe.uiToken, 'current');
  assert.equal(subscribe.payload.params.types[0], 'todo.changed');
  assert.equal(subscribe.payload.params.paperIds[0], 'p');
  deliver({ type: 'hostResponse', requestId: subscribe.payload.requestId, ok: true, result: { subscribed: true } });
  const event = { kind: 'todoChanged', before: { paperId: 'p', id: 't', reminderTriggered: false }, after: { paperId: 'p', id: 't', reminderTriggered: true }, changedFields: 'reminder' };
  deliver({ type: 'hostEvent', subscriptionId: subscribe.payload.params.subscriptionId, event });
  assert.equal(received.length, 1); assert.equal(received[0].after.id, 't');
  stop(); stop();
  assert.equal(posted.length, 1);
  const unsubscribe = posted.shift(); assert.equal(unsubscribe.payload.method, 'events.unsubscribe');
  deliver({ type: 'hostResponse', requestId: unsubscribe.payload.requestId, ok: true, result: {} });
  deliver({ type: 'hostEvent', subscriptionId: subscribe.payload.params.subscriptionId, event });
  assert.equal(received.length, 1);
  api.onHostEvent(['todo.changed'], value => received.push(value));
  const denied = posted.shift();
  deliver({ type: 'hostResponse', requestId: denied.payload.requestId, ok: false, error: { code: 'permission_denied', message: 'Denied' } });
  await Promise.resolve(); await Promise.resolve();
  assert.equal(errors.length, 1); assert.equal(errors[0].error.code, 'permission_denied');
  deliver({ type: 'hostEvent', subscriptionId: denied.payload.params.subscriptionId, event });
  assert.equal(received.length, 1);
})().catch(error => { console.error(error); process.exitCode = 1; });
