// Test-only implementation of the public API 2.1 contract. Never shipped as the real bridge.
(() => {
  const clone = value => structuredClone(value);
  const fixture = window.testHost = {
    calls: [], saves: [], capsules: [], claims: [], providers: [], errors: [], readyCount: 0,
    failRead: false, failWrite: false, failSave: false, delayWrite: false,
    papers: [
      { id: 'p1', title: 'Project', type: 'todo', bodyProviderId: '' },
      { id: 'p2', title: 'Private', type: 'todo', bodyProviderId: '' },
      { id: 'p3', title: 'Reference', type: 'note', bodyProviderId: 'builtin.markdown' },
      { id: 'self', title: 'Starpaper', type: 'note', bodyProviderId: 'com.papertodo.starpaper' }
    ],
    todos: [
      { id: 't1', paperId: 'p1', paperTitle: 'Project', text: 'Read a book', done: false, order: 0 },
      { id: 't2', paperId: 'p1', paperTitle: 'Project', text: 'Write code', done: true, order: 1 },
      { id: 'private', paperId: 'p2', paperTitle: 'Private', text: 'Do not read without selection', done: false, order: 0 }
    ],
    notes: [{ paperId: 'p3', paperTitle: 'Reference', bodyProviderId: 'builtin.markdown', contentAvailable: true, content: 'Live reference text. [[Idea]]' }],
    emit(message) { window.dispatchEvent(new CustomEvent('papertodo', { detail: clone(message) })); },
    change() { fixture.eventHandler?.({ type: 'todo.changed' }); },
    initialize(state = null, surface = 'body') {
      papertodo.surface = surface;
      fixture.emit({ type: 'initialize', state, surface, visible: true, stateVersion: 1, targetStateVersion: 1, settings: { language: 'en' } });
    }
  };
  window.papertodo = {
    surface: 'body',
    saveState(state) { if (fixture.failSave) throw new Error('disk unavailable'); fixture.saves.push(clone(state)); },
    registerStateProvider(fn) { fixture.providers.push(fn); },
    paper: { setHeaderText() {}, setCapsulePresentation(value) { fixture.capsules.push(clone(value)); } },
    body: { setInputClaims(value) { fixture.claims.push(clone(value)); } },
    mini: { ready() { fixture.readyCount++; fixture.readyBox = document.getElementById('app').getBoundingClientRect().toJSON(); } },
    onHostEvent(types, handler, options) { fixture.subscription = { types, options }; fixture.eventHandler = handler; return () => { fixture.eventHandler = null; }; },
    workspace: { async request(method, args = {}) {
      fixture.calls.push({ method, args: clone(args) });
      if (/\.list$|\.get$/.test(method) && fixture.failRead) throw new Error('read failed');
      if (method === 'papers.list') return clone(fixture.papers);
      if (method === 'todos.list') return clone(fixture.todos.filter(t => t.paperId === args.paperId));
      if (method === 'notes.get') return clone(fixture.notes.find(n => n.paperId === args.paperId) || null);
      if (fixture.delayWrite) await new Promise(resolve => { fixture.resolveWrite = resolve; });
      if (fixture.failWrite) throw new Error('write failed');
      if (method === 'todos.update') {
        const item = fixture.todos.find(t => t.id === args.todoId && t.paperId === args.paperId);
        if (!item) throw new Error('not found');
        if (typeof args.text === 'string') item.text = args.text;
        if (typeof args.done === 'boolean') item.done = args.done;
        fixture.change(); return { paperId: args.paperId, todoId: args.todoId };
      }
      if (method === 'todos.append') {
        const todoIds = args.todos.map((item, i) => {
          const id = 'new-' + fixture.todos.length + '-' + i;
          fixture.todos.push({ ...item, id, done: false, order: fixture.todos.length, paperId: args.paperId, paperTitle: 'Project' }); return id;
        });
        fixture.change(); return { paperId: args.paperId, todoIds };
      }
      throw new Error('Unexpected fixture call: ' + method);
    } }
  };
})();
