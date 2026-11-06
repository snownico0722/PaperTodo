const vm = require('node:vm'), fs = require('node:fs'), assert = require('node:assert/strict');
// An unresolved Promise alone does not keep Node alive. Report an incomplete run
// as failure instead of allowing the C# parent to accept a zero exit code.
let completed = false;
process.once('beforeExit', () => {
  if (!completed) {
    console.error('Bridge checks exited before the asynchronous checks completed.');
    process.exitCode = 1;
  }
});
(async () => {
  const posted = [], listeners = [], styles = new Map();
  const scope = {
    location: {origin: 'https://popup.test'},
    document: {documentElement: {style: {setProperty: (k, v) => styles.set(k, v)}}},
    chrome: {webview: {postMessage: value => posted.push(value), addEventListener: (_, fn) => listeners.push(fn)}}
  };
  scope.window = scope; scope.top = scope;
  vm.runInNewContext(fs.readFileSync(process.argv[2], 'utf8'), scope);
  const api = scope.papertodo;
  const deliver = data => listeners.forEach(fn => fn({data}));
  api.popup.close(); assert.equal(posted.length, 0);
  const image = api.noteAssets.readImage('note', 'image');
  await Promise.resolve(); assert.equal(posted.length, 0);
  deliver({type: 'initialize', token: 'document', theme: {paperColor: '#fff', isDark: false}, data: {paperId: 'note'}});
  assert.equal(posted.shift().method, 'popup.close');
  await Promise.resolve();
  let request = posted.shift();
  assert.equal(request.token, 'document'); assert.equal(request.method, 'noteAssets.readImage');
  deliver({type: 'response', requestId: request.requestId, ok: true, result: {bytes: 'AA==', mime: 'image/png'}});
  assert.equal((await image).bytes, 'AA==');
  const denied = api.noteAssets.readImage('other', 'image'); await Promise.resolve();
  request = posted.shift();
  deliver({type: 'response', requestId: request.requestId, ok: false, error: {code: 'permission_denied', message: 'denied'}});
  await assert.rejects(denied, e => e.code === 'permission_denied');
  const message = api.popup.post({value: 'picked'}); await Promise.resolve();
  request = posted.shift(); assert.equal(request.method, 'popup.post');
  deliver({type: 'response', requestId: request.requestId, ok: true, result: {delivered: true}});
  assert.equal((await message).delivered, true);
  api.popup.close(); assert.equal(posted.pop().method, 'popup.close');
  assert.equal(styles.get('--paper-background'), '#fff');
  assert.equal(api.workspace, undefined); assert.equal(api.popups, undefined); assert.equal(api.paperActions, undefined);
  const foreign = {...scope, location: {origin: 'https://external.test'}};
  delete foreign.papertodo; foreign.window = foreign; foreign.top = foreign;
  vm.runInNewContext(fs.readFileSync(process.argv[2], 'utf8'), foreign);
  assert.equal(foreign.papertodo, undefined);
})().then(() => { completed = true; }, error => { completed = true; console.error(error); process.exitCode = 1; });
