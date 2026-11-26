const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

async function run() {
  const sample = path.join(__dirname, '../../plugin-samples/PaperTodo.Plugin.BottomBarWeb/web/runtime.html');
  const script = fs.readFileSync(sample, 'utf8').match(/<script>([\s\S]*?)<\/script>/)[1];
  const published = new Map();
  const todos = [{ done: false }];
  let note = 'A';
  let onRuntime, onChange;
  const events = ['note.changed', 'todo.created', 'todo.changed', 'todo.deleted'];
  const nextTurn = () => new Promise(resolve => setImmediate(resolve));
  vm.runInNewContext(script, { console, papertodo: {
    onEvent: callback => { onRuntime = callback; },
    onHostEvent: (names, callback, filter) => {
      assert.deepEqual([...names].sort(), [...events].sort());
      assert.equal(filter, undefined); // No unbounded ID filter; consult the current target map.
      onChange = callback;
      return () => {};
    },
    bottomBar: {
      listTargets: async () => [{ paperId: 'n', type: 'note' }, { paperId: 't', type: 'todo' }],
      setItems: async (id, items) => published.set(id, items[0].text)
    },
    workspace: { request: async (operation, { paperId }) => {
      if (operation === 'notes.get' && paperId === 'n') return { contentAvailable: true, content: note };
      assert.equal(operation, 'todos.list');
      assert.equal(paperId, 't');
      return todos;
    } }
  } });
  onRuntime({ type: 'initialize' });
  await nextTurn();
  assert.equal(published.get('n'), '1 chars');
  assert.equal(published.get('t'), '1 remaining');
  async function change(event, id, expected) {
    onChange(event);
    await nextTurn();
    assert.equal(published.get(id), expected);
  }
  todos.push({ done: false });
  await change({ type: 'todo.created', todo: { paperId: 't' } }, 't', '2 remaining');
  todos[0].done = true;
  await change({ type: 'todo.changed', after: { paperId: 't' } }, 't', '1 remaining');
  todos.pop();
  await change({ type: 'todo.deleted', todo: { paperId: 't' } }, 't', '0 remaining');
  note = 'A🐦';
  await change({ type: 'note.changed', paperId: 'n' }, 'n', '2 chars');
  console.log('PASS bottom bar sample updates matching paper counts on workspace events');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
