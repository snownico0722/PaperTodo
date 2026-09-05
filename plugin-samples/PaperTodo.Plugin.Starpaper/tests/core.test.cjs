'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const C = require('../web/core.js');
const A = require('../web/art.js');
const I = require('../web/i18n.js');
const paper = { id: 'p1', type: 'todo', title: 'Work' };
const todo = { id: 't1', paperId: 'p1', paperTitle: 'Work', text: 'Read a book', done: false };
const note = { id: 'n:one', title: 'One', body: '[[Two]]' };
const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };

test('empty host state has a complete schema; valid state is not silently rewritten', () => {
  for (const value of [null, undefined, {}]) assert.deepEqual(C.validate(value), C.blank());
  const state = { ...C.blank(), extension: 'preserve me' };
  assert.equal(C.validate(state), state);
});
test('unknown versions and corrupt collections fail closed', () => {
  for (const raw of [{ schema: 2 }, { schema: 0 }, [], '', { ...C.blank(), notes: null }, { ...C.blank(), view: 'secret' }]) assert.throws(() => C.validate(raw));
});
test('duplicate IDs, ambiguous edges, reserved properties, nonfinite geometry are rejected', () => {
  for (const raw of [
    { ...C.blank(), notes: [note, note] }, { ...C.blank(), sources: ['p1', 'p1'] },
    { ...C.blank(), links: [{ from: 'a', to: 'b', label: '' }, { from: 'b', to: 'a', label: '' }] },
    { ...C.blank(), links: [{ from: 'a', to: 'a', label: '' }] },
    { ...C.blank(), positions: { a: { x: Infinity, y: 0 } } },
    { ...C.blank(), positions: JSON.parse('{"__proto__":{"x":0,"y":0}}') }
  ]) assert.throws(() => C.validate(raw));
});
test('JSON budget counts UTF-8 bytes, not UTF-16 character count', () => {
  assert.equal(C.bytes('知'), 3); assert.equal(C.bytes('🌟'), 4);
  assert.throws(() => C.validate({ ...C.blank(), notes: [{ ...note, body: '知'.repeat(4 * 1024 * 1024) }] }), /capacity/);
});
test('image state accepts raster data only; no SVG, external URLs or script URLs', () => {
  assert.equal(C.safeImage('data:image/webp;base64,QUJD'), true);
  for (const image of ['javascript:alert(1)', 'https://example.com/a.jpg', 'data:image/svg+xml;base64,PHN2Zz4=', 'data:text/html;base64,QUJD']) {
    assert.equal(C.safeImage(image), false);
    assert.throws(() => C.validate({ ...C.blank(), covers: { x: { scene: 'orbit', image } } }), /invalidImage/);
  }
});
test('composite task identities cannot collide on delimiters', () => {
  assert.notEqual(C.todoKey('a:b', 'c'), C.todoKey('a', 'b:c'));
  assert.notEqual(C.todoKey('a', '%'), C.todoKey('a', '%25'));
  assert.equal(C.pair('a:b', 'c'), C.pair('c', 'a:b'));
});
test('only selected papers contribute live tasks; host text/completion is never copied into state', () => {
  const state = { ...C.blank(), sources: ['p1'] }, before = JSON.stringify(state);
  const model = C.graph(state, [paper], [todo, { ...todo, id: 'secret', paperId: 'p2' }]);
  assert.equal(model.nodes.filter(n => n.kind === 'todo').length, 1);
  assert.equal(model.byId.get(C.todoKey('p1', 't1')).todo, todo);
  assert.equal(JSON.stringify(state), before);
});
test('Markdown sources are read-only references and can be wiki targets', () => {
  const state = { ...C.blank(), sources: ['p2'], notes: [{ ...note, body: '[[Reference]]' }] };
  const model = C.graph(state, [{ id: 'p2', title: 'Reference', type: 'note' }], [], [{ paperId: 'p2', contentAvailable: true, content: 'Original text' }]);
  assert.equal(model.byId.get(C.paperKey('p2')).kind, 'note-ref');
  assert.equal(model.byId.get(C.paperKey('p2')).body, 'Original text');
  assert.equal(model.edges[0].kind, 'wiki');
  assert.equal(JSON.stringify(state).includes('Original text'), false);
});
test('deleted or unavailable task references remain ghosts without destructive cleanup', () => {
  const key = C.todoKey('p1', 't1'), state = C.setLink({ ...C.blank(), notes: [note], covers: { [key]: { scene: 'read' } } }, note.id, key, 'Read');
  const model = C.graph(state, [], []);
  assert.equal(model.byId.get(key).kind, 'missing'); assert.equal(state.covers[key].scene, 'read');
  assert.equal(model.edges.length, 1);
});
test('completion filter cannot turn a hidden completed task into a missing reference', () => {
  const key = C.todoKey('p1', 't1'), state = C.setLink({ ...C.blank(), sources: ['p1'], notes: [note] }, note.id, key);
  const model = C.graph(state, [paper], [{ ...todo, done: true }]);
  const visible = C.visibleGraph(model, 'open');
  assert.equal(model.byId.get(key).kind, 'todo');
  assert.equal(visible.nodes.some(n => n.id === key || n.kind === 'missing'), false);
  assert.equal(C.visibleGraph(model, 'done').nodes.some(n => n.id === key), true);
});
test('wiki references are exact, NFC-aware, unique and deduplicated with manual links', () => {
  let state = { ...C.blank(), notes: [{ ...note, body: '[[Café]] [[Café]]' }, { id: 'n:two', title: 'Café', body: '' }] };
  assert.equal(C.graph(state, [], []).edges.length, 1);
  state = C.setLink(state, 'n:one', 'n:two', 'Named relationship');
  assert.equal(C.graph(state, [], []).edges.length, 1);
  state = { ...state, links: [], notes: [...state.notes, { id: 'n:three', title: 'Café', body: '' }] };
  assert.equal(C.graph(state, [], []).edges.length, 0);
});
test('manual relation update is undirected, non-mutating, and refuses self-links', () => {
  const original = C.blank(), first = C.setLink(original, 'a', 'b', ' first '), second = C.setLink(first, 'b', 'a', 'second');
  assert.equal(original.links.length, 0); assert.equal(first.links[0].label, 'first');
  assert.equal(second.links.length, 1); assert.equal(second.links[0].label, 'second');
  assert.throws(() => C.setLink(second, 'a', 'a'), /selfLink/);
});
test('deleting a local idea cleans its own metadata but leaves host tasks/other covers alone', () => {
  const key = C.todoKey('p1', 't1');
  const state = C.setLink({ ...C.blank(), notes: [note], sources: ['p1'], covers: { [note.id]: { scene: 'read' }, [key]: { scene: 'code' } }, positions: { [note.id]: { x: 0, y: 0 } } }, note.id, key);
  const next = C.removeNote(state, note.id);
  assert.equal(next.notes.length, 0); assert.equal(next.links.length, 0); assert.deepEqual(next.sources, ['p1']);
  assert.equal(next.covers[key].scene, 'code'); assert.equal(Object.keys(next.positions).length, 0); assert.equal(state.notes.length, 1);
});
test('search and one-hop focus return only edges with visible endpoints', () => {
  const state = C.setLink({ ...C.blank(), sources: ['p1'], notes: [note] }, note.id, C.todoKey('p1', 't1'));
  const model = C.graph(state, [paper], [todo]);
  assert.equal(C.visibleGraph(model, 'all', 'book').nodes.length, 1);
  assert.equal(C.visibleGraph(model, 'all', '', note.id).nodes.length, 2);
  assert.equal(C.visibleGraph(model, 'all', 'missing').nodes.length, 0);
});
test('layout is deterministic, finite and never truncates 2500 nodes', () => {
  const state = { ...C.blank(), sources: ['p1'], notes: [note] };
  const tasks = Array.from({ length: 2500 }, (_, i) => ({ ...todo, id: 'task-' + i }));
  const graph = C.graph(state, [paper], tasks), a = C.layout(graph), b = C.layout(graph);
  assert.deepEqual(a, b); assert.equal(a.size, 2502);
  for (const pos of a.values()) assert.ok(Number.isFinite(pos.x) && Number.isFinite(pos.y));
  const stored = { [note.id]: { x: 321, y: -55 } }; assert.deepEqual(C.layout(graph, stored).get(note.id), stored[note.id]);
  const fit = C.fit(graph.nodes, a, 320, 240); assert.ok(Object.values(fit).every(Number.isFinite));
});
test('zoom preserves the world coordinate underneath the pointer and clamps scale', () => {
  const before = { x: 23, y: -51, k: .7 }, after = C.zoom(before, 1.3, 120, 200);
  assert.ok(Math.abs((120 - before.x) / before.k - (120 - after.x) / after.k) < 1e-8);
  assert.equal(C.zoom(before, 1e9, 120, 200).k, 4); assert.equal(C.zoom(before, 1e-9, 120, 200).k, .01);
});
test('history has a bounded depth; undo and redo do not own host data', () => {
  const h = new C.History(3, 100000), states = Array.from({ length: 6 }, (_, i) => ({ ...C.blank(), wiki: i % 2 === 0 }));
  states.slice(0, 5).forEach(s => h.push(s)); assert.equal(h.past.length, 3);
  const previous = h.undo(states[5]); assert.equal(previous, states[4]); assert.equal(h.redo(previous), states[5]);
  h.push(states[0]); assert.equal(h.future.length, 0); h.clear(); assert.equal(h.past.length, 0);
});
test('refresh queue rejects obsolete responses and publishes only the latest complete snapshot', async () => {
  const loads = [], published = [], errors = [];
  const q = new C.RefreshQueue(() => { const job = deferred(); loads.push(job); return job.promise; }, v => published.push(v), e => errors.push(e));
  const idle = q.request(); q.request(); q.request(); assert.equal(loads.length, 1);
  loads[0].resolve('old'); await tick(); assert.equal(loads.length, 2); assert.deepEqual(published, []);
  loads[1].resolve('new'); await idle; assert.deepEqual(published, ['new']); assert.deepEqual(errors, []);
});
test('read failure keeps the last snapshot; refresh is explicit, not an infinite retry', async () => {
  let count = 0; const published = [], errors = [];
  const q = new C.RefreshQueue(async () => { if (++count === 2) throw new Error('offline'); return count; }, v => published.push(v), e => errors.push(e.message));
  await q.request(); await q.request(); await tick(); assert.equal(count, 2); assert.deepEqual(published, [1]); assert.deepEqual(errors, ['offline']);
  await q.request(); assert.deepEqual(published, [1, 3]);
});
test('disposing a refresh queue prevents late publication and further reads', async () => {
  const job = deferred(), published = []; let calls = 0;
  const q = new C.RefreshQueue(() => { calls++; return job.promise; }, v => published.push(v), () => assert.fail('disposed error'));
  const idle = q.request(); q.dispose(); job.resolve('late'); await idle; await q.request();
  assert.equal(calls, 1); assert.deepEqual(published, []);
});
test('every UI string has four translations and each art scene is deterministic and local', () => {
  for (const [key, row] of Object.entries(I.rows)) assert.ok(row.length === 4 && row.every(s => typeof s === 'string' && s.length), key);
  assert.equal(I.language('auto', 'ja-JP'), 'ja'); assert.equal(I.language('auto', 'fr-FR'), 'en');
  for (const scene of C.SCENES) {
    assert.equal(A.svg(scene, 42), A.svg(scene, 42)); assert.ok(A.data(scene, 42).startsWith('data:image/svg+xml'));
    assert.equal(/<script|<foreignObject|https:\/\//.test(A.svg(scene, 42)), false);
  }
  assert.equal(C.sceneFor('Read a book'), 'read'); assert.equal(C.sceneFor('插件测试'), 'code');
});
