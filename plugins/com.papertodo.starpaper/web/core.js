/* Starpaper's data model is independent of the DOM and the PaperTodo transport. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.StarCore = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const SCHEMA = 1;
  const MAX_BYTES = 10 * 1024 * 1024; // PaperTodo API 2.1 per-paper JSON limit.
  const SCENES = Object.freeze(['orbit', 'read', 'code', 'grow', 'travel', 'health', 'create', 'focus']);
  const bytes = value => new TextEncoder().encode(typeof value === 'string' ? value : JSON.stringify(value)).length;
  const own = (object, key) => Object.prototype.hasOwnProperty.call(object, key);
  const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
  const fail = code => { throw new Error(code); };
  const id = value => typeof value === 'string' && value.length > 0 && value.length <= 512 &&
    !['__proto__', 'constructor', 'prototype'].includes(value);
  function blank() {
    return { schema: SCHEMA, sources: [], notes: [], links: [], positions: {}, covers: {},
      view: 'map', filter: 'all', wiki: true };
  }
  function safeImage(value) {
    return typeof value === 'string' && /^data:image\/(?:png|jpeg|webp);base64,[A-Za-z0-9+/]+={0,2}$/.test(value);
  }
  // Fail closed: invalid/future state is never normalized into an empty writable document.
  function validate(raw) {
    if (raw == null || (record(raw) && Object.keys(raw).length === 0)) return blank();
    if (!record(raw) || raw.schema !== SCHEMA) fail('stateVersion');
    for (const key of ['sources', 'notes', 'links']) if (!Array.isArray(raw[key])) fail('invalidState');
    if (!record(raw.positions) || !record(raw.covers)) fail('invalidState');
    if (!['map', 'cards'].includes(raw.view) || !['all', 'open', 'done'].includes(raw.filter) || typeof raw.wiki !== 'boolean') fail('invalidState');
    const seen = new Set();
    for (const source of raw.sources) {
      if (!id(source) || seen.has(source)) fail('invalidState');
      seen.add(source);
    }
    seen.clear();
    for (const note of raw.notes) {
      if (!record(note) || !id(note.id) || !note.id.startsWith('n:') || seen.has(note.id) ||
          typeof note.title !== 'string' || !note.title.trim() || typeof note.body !== 'string') fail('invalidState');
      seen.add(note.id);
    }
    seen.clear();
    for (const link of raw.links) {
      if (!record(link) || !id(link.from) || !id(link.to) || link.from === link.to || typeof link.label !== 'string') fail('invalidState');
      const key = pair(link.from, link.to);
      if (seen.has(key)) fail('invalidState');
      seen.add(key);
    }
    for (const [key, pos] of Object.entries(raw.positions)) {
      if (!id(key) || !record(pos) || !Number.isFinite(pos.x) || !Number.isFinite(pos.y) ||
          Math.abs(pos.x) > 1e7 || Math.abs(pos.y) > 1e7) fail('invalidState');
    }
    for (const [key, cover] of Object.entries(raw.covers)) {
      if (!id(key) || !record(cover) || !SCENES.includes(cover.scene) ||
          (cover.image !== undefined && cover.image !== null && !safeImage(cover.image))) fail('invalidImage');
    }
    if (bytes(raw) > MAX_BYTES) fail('capacity');
    // A versioned file is a complete document. Return it unchanged: no silent field loss.
    return raw;
  }
  function pair(a, b) { return JSON.stringify(a < b ? [a, b] : [b, a]); }
  const todoKey = (paperId, todoId) => `t:${encodeURIComponent(paperId)}:${encodeURIComponent(todoId)}`;
  const paperKey = paperId => `p:${encodeURIComponent(paperId)}`;
  function hash(text) {
    let h = 2166136261;
    for (const c of String(text)) { h ^= c.codePointAt(0); h = Math.imul(h, 16777619); }
    return h >>> 0;
  }
  function sceneFor(text) {
    const groups = [
      ['read', /读|阅读|书|学习|论文|read|book|learn|study|読|勉強|독서|공부/i],
      ['code', /代码|编程|修复|测试|插件|code|bug|test|develop|プログラム|코드|개발/i],
      ['grow', /植物|花|园|种植|grow|plant|garden|花|植物|식물|정원/i],
      ['travel', /旅行|出游|机票|游览|出发|travel|trip|flight|旅|여행/i],
      ['health', /运动|健身|跑|训练|散步|workout|health|gym|run|運動|운동/i],
      ['create', /画|设计|创作|写作|音乐|拍摄|draw|design|write|music|写真|그림|디자인/i],
      ['focus', /专注|整理|规划|计划|focus|plan|organize|計画|집중|계획/i]
    ];
    return groups.find(([, re]) => re.test(text))?.[0] || 'orbit';
  }
  function setLink(state, from, to, label = '') {
    if (!id(from) || !id(to) || from === to) fail('selfLink');
    const links = state.links.filter(link => pair(link.from, link.to) !== pair(from, to));
    return { ...state, links: [...links, { from, to, label: String(label).trim() }] };
  }
  function removeNote(state, noteId) {
    const positions = { ...state.positions }, covers = { ...state.covers };
    delete positions[noteId]; delete covers[noteId];
    return { ...state, notes: state.notes.filter(n => n.id !== noteId), positions, covers,
      links: state.links.filter(e => e.from !== noteId && e.to !== noteId) };
  }
  function wikiTargets(text) {
    return [...String(text).matchAll(/\[\[([^\[\]\n]+)\]\]/g)].map(match => match[1].trim()).filter(Boolean);
  }
  function graph(state, papers, todos, sourceNotes = []) {
    const nodes = [], edges = [], byId = new Map();
    function add(node) { if (!byId.has(node.id)) { nodes.push(node); byId.set(node.id, node); } }
    for (const source of state.sources) {
      const paper = papers.find(p => p.id === source);
      const note = sourceNotes.find(n => n.paperId === source);
      add({ id: paperKey(source), kind: paper?.type === 'note' ? 'note-ref' : 'paper',
        title: paper?.title || '', body: note?.contentAvailable ? note.content : '',
        paperId: source, missing: !paper, unavailable: paper?.type === 'note' && !note?.contentAvailable });
    }
    for (const todo of todos) {
      if (!state.sources.includes(todo.paperId)) continue;
      const key = todoKey(todo.paperId, todo.id);
      add({ id: key, kind: 'todo', title: todo.text, body: todo.text, done: !!todo.done, todo,
        scene: state.covers[key]?.scene || sceneFor(todo.text) });
      edges.push({ from: paperKey(todo.paperId), to: key, kind: 'contains', label: '' });
    }
    for (const note of state.notes) add({ ...note, kind: 'note', scene: state.covers[note.id]?.scene || sceneFor(note.title) });
    // A missing external reference is not proof of deletion. Keep it visible, never prune its data.
    for (const link of state.links) {
      for (const key of [link.from, link.to]) if (!byId.has(key)) add({ id: key, kind: 'missing', title: '', missing: true });
      edges.push({ ...link, kind: 'manual' });
    }
    if (state.wiki) {
      const titles = new Map(), used = new Set(edges.map(e => pair(e.from, e.to)));
      for (const node of nodes.filter(n => n.kind === 'note' || n.kind === 'note-ref')) {
        const key = node.title.trim().normalize('NFC');
        titles.set(key, titles.has(key) ? null : node.id); // Ambiguous names never create guessed facts.
      }
      for (const node of nodes) for (const target of wikiTargets(node.body || '')) {
        const to = titles.get(target.normalize('NFC'));
        if (!to || to === node.id || used.has(pair(node.id, to))) continue;
        used.add(pair(node.id, to)); edges.push({ from: node.id, to, kind: 'wiki', label: '' });
      }
    }
    return { nodes, edges, byId };
  }
  function visibleGraph(model, filter, query = '', focusId = null) {
    const queryText = query.trim().toLocaleLowerCase();
    let allowed = new Set(model.nodes.filter(n =>
      (n.kind !== 'todo' || filter === 'all' || (filter === 'done') === n.done) &&
      (!queryText || `${n.title}\n${n.body || ''}`.toLocaleLowerCase().includes(queryText))).map(n => n.id));
    if (focusId && model.byId.has(focusId)) {
      const neighborhood = new Set([focusId]);
      model.edges.forEach(e => { if (e.from === focusId) neighborhood.add(e.to); if (e.to === focusId) neighborhood.add(e.from); });
      allowed = new Set([...allowed].filter(key => neighborhood.has(key)));
    }
    return { nodes: model.nodes.filter(n => allowed.has(n.id)), edges: model.edges.filter(e => allowed.has(e.from) && allowed.has(e.to)) };
  }
  function layout(model, positions = {}) {
    const result = new Map(), papers = model.nodes.filter(n => n.kind === 'paper');
    const counts = papers.map(p => model.nodes.filter(n => n.todo?.paperId === p.paperId).length);
    const gap = 430 + Math.sqrt(Math.max(0, ...counts)) * 110;
    const cols = Math.max(1, Math.ceil(Math.sqrt(papers.length + 1)));
    papers.forEach((paper, i) => {
      const center = { x: (i % cols + 1) * gap, y: Math.floor(i / cols) * gap };
      result.set(paper.id, center);
      const tasks = model.nodes.filter(n => n.todo?.paperId === paper.paperId).sort((a, b) => a.id.localeCompare(b.id));
      tasks.forEach((node, j) => {
        const angle = j * 2.3999632297 + (hash(paper.id) % 100) / 100;
        const radius = 130 + Math.sqrt(j) * 74;
        result.set(node.id, { x: center.x + Math.cos(angle) * radius, y: center.y + Math.sin(angle) * radius });
      });
    });
    const others = model.nodes.filter(n => !result.has(n.id)).sort((a, b) => a.id.localeCompare(b.id));
    others.forEach((node, i) => {
      const angle = i * 2.3999632297 - Math.PI / 2, radius = others.length === 1 ? 0 : 125 + Math.sqrt(i) * 80;
      result.set(node.id, { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius });
    });
    for (const [key, pos] of Object.entries(positions)) if (result.has(key)) result.set(key, { ...pos });
    return result;
  }
  function fit(nodes, positions, width, height) {
    if (!nodes.length) return { x: width / 2, y: height / 2, k: 1 };
    let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
    for (const n of nodes) {
      const p = positions.get(n.id); if (!p) continue;
      x1 = Math.min(x1, p.x - 95); y1 = Math.min(y1, p.y - 58);
      x2 = Math.max(x2, p.x + 95); y2 = Math.max(y2, p.y + 74);
    }
    const k = Math.min(1.25, Math.max(.01, Math.min(Math.max(40, width - 64) / (x2 - x1), Math.max(40, height - 64) / (y2 - y1))));
    return { x: width / 2 - (x1 + x2) / 2 * k, y: height / 2 - (y1 + y2) / 2 * k, k };
  }
  function zoom(camera, factor, x, y) {
    const k = Math.max(.01, Math.min(4, camera.k * factor)), ratio = k / camera.k;
    return { k, x: x - (x - camera.x) * ratio, y: y - (y - camera.y) * ratio };
  }
  class History {
    constructor(limit = 30, budget = 24 * 1024 * 1024) { this.limit = limit; this.budget = budget; this.clear(); }
    clear() { this.past = []; this.future = []; }
    push(state) {
      this.past.push({ state, size: bytes(state) }); this.future = [];
      let size = this.past.reduce((sum, entry) => sum + entry.size, 0);
      while (this.past.length > 1 && (this.past.length > this.limit || size > this.budget)) size -= this.past.shift().size;
    }
    undo(current) {
      if (!this.past.length) return current;
      this.future.push({ state: current, size: bytes(current) }); return this.past.pop().state;
    }
    redo(current) {
      if (!this.future.length) return current;
      this.past.push({ state: current, size: bytes(current) }); return this.future.pop().state;
    }
  }
  // Coalesce refreshes, but never publish an obsolete response after a newer event/source selection.
  class RefreshQueue {
    constructor(load, publish, onError) { this.load = load; this.publish = publish; this.onError = onError; this.revision = 0; this.running = false; this.disposed = false; }
    request() {
      if (this.disposed) return Promise.resolve();
      this.revision++;
      if (!this.running) this.idle = this.drain();
      return this.idle;
    }
    async drain() {
      if (this.running || this.disposed) return;
      this.running = true;
      try {
        let done;
        do {
          const revision = this.revision;
          try {
            const value = await this.load();
            if (!this.disposed && revision === this.revision) this.publish(value);
          } catch (error) { if (!this.disposed && revision === this.revision) this.onError(error); }
          done = revision === this.revision;
        } while (!done && !this.disposed);
      } finally { this.running = false; }
    }
    dispose() { this.disposed = true; }
  }
  return Object.freeze({ SCHEMA, MAX_BYTES, SCENES, blank, validate, bytes, safeImage, pair, todoKey, paperKey,
    hash, sceneFor, setLink, removeNote, wikiTargets, graph, visibleGraph, layout, fit, zoom, History, RefreshQueue, own });
});
