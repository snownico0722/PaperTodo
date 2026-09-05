(function () {
  'use strict';
  const C = window.StarCore, A = window.StarArt, I = window.StarI18n;
  const $ = id => document.getElementById(id);
  const app = $('app'), dialog = $('dialog');
  const NS = 'http://www.w3.org/2000/svg';
  const demo = document.body.dataset.preview === 'true' && !window.papertodo && !location.hostname.endsWith('.papertodo.local');
  let host = window.papertodo;
  let state = C.blank(), original = null, ready = false, blocked = false, mini = false, visible = true;
  let locale = I.language('auto', navigator.language), t = I.translator(locale);
  let papers = [], todos = [], sourceNotes = [], model = C.graph(state, [], []), positions = new Map();
  let selection = null, focusId = null, query = '', camera = { x: 0, y: 0, k: 1 }, fitNeeded = true;
  let size = { width: 600, height: 400 }, drag = null, suppressClick = false, frame = 0, toastTimer = 0;
  let queue = null, unsubscribe = null, resize = null, stale = false, mounted = false, miniReady = false, workspaceLoaded = false;
  let imageTarget = null, imageJob = 0, generation = 0, lastFocused = null;
  const pending = new Set(), history = new C.History();
  function el(tag, text, className) {
    const node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (className) node.className = className;
    return node;
  }
  function svgEl(tag, attrs = {}, text) {
    const node = document.createElementNS(NS, tag);
    for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, value);
    if (text !== undefined) node.textContent = text;
    return node;
  }
  function button(text, action, className = '') {
    const node = el('button', text, className); node.type = 'button';
    node.addEventListener('click', action); return node;
  }
  function short(text, length = 23) {
    const chars = [...String(text)]; return chars.length > length ? chars.slice(0, length - 1).join('') + '…' : chars.join('');
  }
  function nodeTitle(node) { return node.title || t(node.missing ? 'unavailable' : 'untitled'); }
  function scene(node) { return state.covers[node.id]?.scene || node.scene || C.sceneFor(node.title); }
  function image(node) { return state.covers[node.id]?.image || A.data(scene(node), C.hash(node.id)); }
  function toast(key, detail = '') {
    const message = I.rows[key] ? t(key) : key;
    $('toast').textContent = message + (detail ? ` (${short(detail, 140)})` : '');
    $('toast').hidden = false; clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { $('toast').hidden = true; }, 6500);
  }
  function problem(error) { toast(I.rows[error?.message] ? error.message : 'invalidState', I.rows[error?.message] ? '' : error?.message || ''); }
  function translate() {
    document.documentElement.lang = locale;
    document.querySelectorAll('[data-i18n]').forEach(node => { node.textContent = t(node.dataset.i18n); });
    document.querySelectorAll('[data-title]').forEach(node => { node.title = t(node.dataset.title); node.setAttribute('aria-label', t(node.dataset.title)); });
    if ($('search')) $('search').placeholder = t('search');
  }
  function theme(value) {
    if (!value) return;
    const style = document.documentElement.style;
    const mapping = { paperColor: '--paper', textColor: '--text', weakTextColor: '--weak', accentColor: '--accent', borderColor: '--border' };
    for (const [key, name] of Object.entries(mapping)) if (typeof value[key] === 'string' && CSS.supports('color', value[key])) style.setProperty(name, value[key]);
    if (value.fontFamily) style.setProperty('--font', `${JSON.stringify(value.fontFamily)}, system-ui, sans-serif`);
    if (Number.isFinite(value.fontScale)) style.setProperty('--host-scale', String(Math.max(.5, Math.min(3, value.fontScale))));
    const color = getComputedStyle(document.documentElement).getPropertyValue('--paper').trim();
    if (/^#[\da-f]{6}$/i.test(color)) {
      const n = parseInt(color.slice(1), 16), light = ((n >> 16) * .2126 + ((n >> 8) & 255) * .7152 + (n & 255) * .0722) > 140;
      style.colorScheme = light ? 'light' : 'dark';
    }
  }
  function claims() {
    if (!mini && ready && host?.body?.setInputClaims) host.body.setInputClaims(dialog.open || selection || focusId ? ['escapeKey'] : []);
  }
  function commit(next, remember = true) {
    if (!ready || blocked || mini) return false;
    try {
      C.validate(next);
      const previous = state;
      // saveState is transport submission, not a durable disk-write acknowledgement.
      host.saveState(next);
      if (remember) history.push(previous);
      state = next; original = state;
      render();
      return true;
    } catch (error) { problem(error); return false; }
  }
  function undo(redo = false) {
    const stack = redo ? history.future : history.past;
    if (!stack.length) return;
    const previous = state, entry = stack[stack.length - 1];
    const next = { ...entry.state, view: state.view, filter: state.filter };
    if (commit(next, false)) {
      imageJob++;
      if (redo) history.redo(previous); else history.undo(previous);
      queue.request(); render();
    }
  }
  function mount() {
    if (mounted) return;
    mounted = true;
    app.innerHTML = `<div class="shell">
      <div class="demo-banner" id="demo-banner" hidden data-i18n="demo"></div>
      <header class="top">
        <div class="brand-row"><div class="brand"><span class="brand-mark" aria-hidden="true">✦</span><span data-i18n="name"></span><span class="local-badge" data-i18n="local"></span></div>
          <div class="actions"><button type="button" class="icon" id="undo" data-title="undo">↶</button><button type="button" class="icon" id="redo" data-title="redo">↷</button>
            <details class="menu" id="menu"><summary data-title="more">⋯</summary><div class="menu-items">
              <button type="button" id="export" data-i18n="export"></button><button type="button" id="import" data-i18n="import"></button><button type="button" id="export-svg" data-i18n="exportSvg"></button>
              <label><input type="checkbox" id="wiki"><span data-i18n="wiki"></span></label><button type="button" id="help" data-i18n="help"></button>
            </div></details></div></div>
        <div class="intro"><h1 data-i18n="tagline"></h1><p data-i18n="savedByHost"></p></div>
        <div class="tools"><div class="tabs" role="tablist"><button type="button" id="tab-map" role="tab" aria-controls="map-pane" data-i18n="map"></button><button type="button" id="tab-cards" role="tab" aria-controls="cards-pane" data-i18n="cards"></button></div>
          <div class="actions"><button type="button" id="add-note" class="primary">＋ <span data-i18n="newNote"></span></button><button type="button" id="add-todo" class="secondary">＋ <span data-i18n="newTodo"></span></button></div></div>
        <div class="search-row"><input id="search" type="search" autocomplete="off" data-title="search"><select id="filter" data-title="all"><option value="all" data-i18n="all"></option><option value="open" data-i18n="open"></option><option value="done" data-i18n="done"></option></select><button type="button" id="sources" class="source-button secondary" data-i18n="sources"></button><button type="button" id="refresh" class="icon" data-title="refresh">↻</button></div>
      </header>
      <div class="status-banner" id="status-banner" hidden data-i18n="stale"></div>
      <main class="stage" id="stage">
        <div class="map-pane" id="map-pane" role="tabpanel" aria-labelledby="tab-map"><svg id="canvas" role="region" tabindex="0" data-title="hint"><g id="world"></g></svg><div id="map-empty" class="empty" hidden></div>
          <span class="map-hint" data-i18n="hint"></span><div class="map-controls"><button type="button" id="unfocus" hidden data-i18n="unfocus"></button><button type="button" id="zoom-out" data-title="zoomOut">−</button><button type="button" id="fit" data-i18n="fit"></button><button type="button" id="zoom-in" data-title="zoomIn">＋</button><button type="button" id="arrange" data-i18n="arrange"></button></div></div>
        <div class="cards-pane" id="cards-pane" role="tabpanel" aria-labelledby="tab-cards" hidden><div class="card-grid" id="card-grid"></div><div class="empty" id="cards-empty" hidden></div></div>
        <aside class="inspector" id="inspector" hidden></aside>
      </main>
      <footer class="bottom"><span id="stats"></span><span class="legend"><span data-i18n="knowledge"></span><span data-i18n="tasks"></span></span></footer>
    </div>`;
    $('demo-banner').hidden = !demo;
    $('undo').onclick = () => undo(); $('redo').onclick = () => undo(true);
    $('add-note').onclick = () => editNote(); $('add-todo').onclick = () => newTodo();
    $('sources').onclick = chooseSources; $('refresh').onclick = () => queue.request();
    $('search').addEventListener('input', e => { query = e.target.value; fitNeeded = true; render(); });
    $('filter').onchange = e => { fitNeeded = true; commit({ ...state, filter: e.target.value }, false); };
    for (const view of ['map', 'cards']) $(`tab-${view}`).onclick = () => { commit({ ...state, view }, false); schedulePaint(); };
    $('fit').onclick = fitAll;
    $('zoom-in').onclick = () => { camera = C.zoom(camera, 1.25, size.width / 2, size.height / 2); schedulePaint(); };
    $('zoom-out').onclick = () => { camera = C.zoom(camera, .8, size.width / 2, size.height / 2); schedulePaint(); };
    $('arrange').onclick = () => confirmDialog('arrange', 'arrangeHint', () => { fitNeeded = true; return commit({ ...state, positions: {} }); });
    $('unfocus').onclick = () => { focusId = null; fitNeeded = true; render(); };
    $('export').onclick = () => downloadJson(state);
    $('import').onclick = () => { $('backup-file').value = ''; $('backup-file').click(); };
    $('export-svg').onclick = exportMap;
    $('wiki').onchange = e => commit({ ...state, wiki: e.target.checked });
    $('help').onclick = () => openForm('help', area => { area.append(el('p', t('helpText')), el('p', t('savedByHost'), 'small')); if (demo) area.append(el('p', t('previewOnly'), 'small')); }, () => true, 'close');
    $('menu').querySelectorAll('button').forEach(node => node.addEventListener('click', () => { $('menu').open = false; }));
    bindCanvas();
    resize = new ResizeObserver(() => {
      if (state.view !== 'map' || !visible) return;
      const rect = $('map-pane').getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      camera.x += (rect.width - size.width) / 2; camera.y += (rect.height - size.height) / 2;
      size = { width: rect.width, height: rect.height }; schedulePaint();
    });
    resize.observe($('map-pane'));
    translate();
  }
  function rebuild() {
    model = C.graph(state, papers, todos, sourceNotes); positions = C.layout(model, state.positions);
    if (selection && !model.byId.has(selection)) selection = null;
    if (focusId && !model.byId.has(focusId)) focusId = null;
  }
  function render() {
    if (!ready || blocked) return;
    app.setAttribute('aria-busy', 'false');
    rebuild();
    if (mini) { renderMini(); return; }
    mount();
    $('tab-map').setAttribute('aria-selected', state.view === 'map');
    $('tab-cards').setAttribute('aria-selected', state.view === 'cards');
    $('map-pane').hidden = state.view !== 'map'; $('cards-pane').hidden = state.view !== 'cards';
    $('filter').value = state.filter; $('wiki').checked = state.wiki;
    $('undo').disabled = !history.past.length; $('redo').disabled = !history.future.length;
    $('add-todo').disabled = stale || pending.has('append');
    $('status-banner').hidden = !stale; $('unfocus').hidden = !focusId;
    const taskNodes = model.nodes.filter(n => n.kind === 'todo'), done = taskNodes.filter(n => n.done).length;
    const ideas = model.nodes.filter(n => n.kind === 'note' || n.kind === 'note-ref').length;
    $('stats').textContent = `${ideas} ${t('knowledge')} · ${done}/${taskNodes.length} ${t('tasks')} · ${state.sources.length} ${t('papers')}`;
    if (visible) { if (state.view === 'cards') renderCards(); renderInspector(); schedulePaint(); }
    const caption = `${t('name')} · ${taskNodes.length - done} ${t('open')} · ${ideas} ${t('knowledge')}`;
    host.paper.setHeaderText(caption);
    host.paper.setCapsulePresentation({ preferredWidth: 0, plainText: caption, components: [{ kind: 'text', text: `✦ ${taskNodes.length - done} · ${ideas}`, fill: true }] });
    claims();
  }
  function emptyContent(container, filtered = false, cards = false) {
    container.replaceChildren(el('div', '✦', 'empty-mark'), el('h2', t(filtered ? 'noMatch' : cards ? 'noTasks' : 'emptyTitle')), el('p', t('emptyText')));
    const actions = el('div', undefined, 'actions');
    if (filtered) actions.append(button(t('clearFilter'), () => { query = ''; $('search').value = ''; focusId = null; fitNeeded = true; commit({ ...state, filter: 'all' }, false); }, 'secondary'));
    else actions.append(button(t(cards ? 'newTodo' : 'newNote'), () => cards ? newTodo() : editNote(), 'primary'), button(t('sources'), chooseSources, 'secondary'));
    container.append(actions);
  }
  function renderCards() {
    const nodes = C.visibleGraph(model, state.filter, query).nodes.filter(n => n.kind === 'todo');
    const focused = document.activeElement, focusedId = focused?.closest('[data-card]')?.dataset.card;
    const focusedAction = focused?.dataset.action;
    const grid = $('card-grid'); grid.replaceChildren();
    $('cards-empty').hidden = nodes.length !== 0;
    if (!nodes.length) emptyContent($('cards-empty'), !!query || state.filter !== 'all', true);
    for (const node of nodes) {
      const card = el('article', undefined, `task-card${node.done ? ' done' : ''}${selection === node.id ? ' selected' : ''}`);
      const cover = button('', () => select(node.id), 'card-art'); cover.setAttribute('aria-label', nodeTitle(node));
      const img = el('img'); img.src = image(node); img.alt = ''; img.loading = 'lazy'; img.width = 320; img.height = 210; cover.append(img);
      const content = el('div', undefined, 'card-content');
      content.append(el('div', node.todo.paperTitle || t('untitled'), 'card-meta'), button(node.title, () => select(node.id), 'card-title'), completion(node));
      card.dataset.card = node.id; card.append(cover, content); grid.append(card);
      if (focusedId === node.id && focusedAction === 'completion') card.querySelector('[data-action=completion]').focus({ preventScroll: true });
    }
  }
  function completion(node) {
    const control = button('', () => mutate(node.id, 'todos.update', { paperId: node.todo.paperId, todoId: node.todo.id, done: !node.done }), 'completion');
    control.dataset.action = 'completion';
    control.setAttribute('role', 'checkbox'); control.setAttribute('aria-checked', String(node.done));
    control.setAttribute('aria-label', `${t(node.done ? 'reopen' : 'complete')}: ${node.title}`);
    control.disabled = stale || pending.has(node.id);
    if (pending.has(node.id)) control.setAttribute('aria-busy', 'true');
    control.append(el('span', pending.has(node.id) ? '·' : node.done ? '✓' : '', 'checkmark'), el('span', t(node.done ? 'done' : 'open')));
    return control;
  }
  function select(id) { selection = id; render(); }
  function renderInspector() {
    const area = $('inspector'), node = model.byId.get(selection);
    area.hidden = !node; area.replaceChildren(); if (!node) return;
    const heading = el('div', undefined, 'inspector-head');
    heading.append(el('span', t(['note', 'note-ref'].includes(node.kind) ? 'knowledge' : node.kind === 'todo' ? 'tasks' : node.kind === 'paper' ? 'papers' : 'unavailable')), button('×', () => { selection = null; render(); }, 'icon'));
    heading.lastChild.setAttribute('aria-label', t('close')); area.append(heading);
    if (['note', 'note-ref', 'todo'].includes(node.kind)) {
      const img = el('img', undefined, 'inspector-art'); img.src = image(node); img.alt = ''; area.append(img);
    }
    area.append(el('h2', nodeTitle(node)));
    if (node.kind === 'todo') area.append(el('p', node.todo.paperTitle || t('untitled'), 'small'), completion(node));
    if (node.missing) area.append(el('p', t('missingHint'), 'small'));
    if (node.kind === 'note-ref') area.append(el('p', t('readOnlyNote'), 'small'));
    if (node.unavailable) area.append(el('p', t('unavailable'), 'small'));
    if (['note', 'note-ref'].includes(node.kind) && node.body) area.append(el('div', node.body, 'inspector-text'));
    const actions = el('div', undefined, 'actions');
    if (node.kind === 'note' || node.kind === 'todo') {
      const edit = button(t('edit'), () => node.kind === 'note' ? editNote(node) : editTodo(node), 'secondary');
      edit.disabled = node.kind === 'todo' && (stale || pending.has(node.id)); actions.append(edit);
    }
    actions.append(button(t('link'), () => editLink(node.id), 'secondary'));
    actions.append(button(t(focusId === node.id ? 'unfocus' : 'focus'), () => { focusId = focusId === node.id ? null : node.id; fitNeeded = true; render(); }, 'secondary'));
    area.append(actions);
    if (['note', 'note-ref', 'todo'].includes(node.kind)) {
      const section = el('section'); section.append(el('h3', t('illustration')));
      const picker = el('select'); picker.setAttribute('aria-label', t('illustration'));
      C.SCENES.forEach(value => { const option = el('option', t(value === 'focus' ? 'focusScene' : value)); option.value = value; picker.append(option); });
      picker.value = scene(node);
      picker.onchange = () => { imageJob++; commit({ ...state, covers: { ...state.covers, [node.id]: { scene: picker.value } } }); };
      section.append(picker, button(t('image'), () => { imageTarget = node.id; $('image-file').value = ''; $('image-file').click(); }, 'secondary wide'), el('p', t('imageHint'), 'small'));
      section.append(button(t('resetCover'), () => { imageJob++; const covers = { ...state.covers }; delete covers[node.id]; commit({ ...state, covers }); }, 'wide'));
      section.append(button(t('exportCard'), () => exportCard(node).catch(problem), 'wide'));
      area.append(section);
    }
    const links = model.edges.filter(e => e.from === node.id || e.to === node.id);
    if (links.length) {
      const section = el('section'); section.append(el('h3', t('related')));
      links.forEach(edge => {
        const target = model.byId.get(edge.from === node.id ? edge.to : edge.from), row = el('div', undefined, 'connection');
        const targetButton = button(nodeTitle(target), () => select(target.id), 'link-target');
        targetButton.title = nodeTitle(target); targetButton.append(el('small', edge.label || t(edge.kind === 'contains' ? 'contains' : edge.kind === 'wiki' ? 'reference' : 'manual')));
        row.append(targetButton);
        if (edge.kind === 'manual') {
          const remove = button('×', () => commit({ ...state, links: state.links.filter(e => C.pair(e.from, e.to) !== C.pair(edge.from, edge.to)) }), 'icon');
          remove.setAttribute('aria-label', t('unlink')); row.append(remove);
        }
        section.append(row);
      });
      area.append(section);
    }
    if (node.kind === 'note') {
      const section = el('section'); section.append(button(t('remove'), () => confirmDialog('remove', 'removeHint', () => commit(C.removeNote(state, node.id)), true), 'danger wide')); area.append(section);
    }
  }
  function schedulePaint() {
    if (frame || !visible || mini || !mounted || state.view !== 'map') return;
    frame = requestAnimationFrame(() => { frame = 0; paint(); });
  }
  function fitAll() { fitNeeded = true; schedulePaint(); }
  function paint() {
    if (!visible || state.view !== 'map' || !mounted) return;
    const rect = $('map-pane').getBoundingClientRect(); if (!rect.width || !rect.height) return;
    size = { width: rect.width, height: rect.height };
    const filtered = C.visibleGraph(model, state.filter, query, focusId);
    if (fitNeeded) { camera = C.fit(filtered.nodes, positions, size.width, size.height); fitNeeded = false; }
    $('map-empty').hidden = filtered.nodes.length !== 0;
    if (!filtered.nodes.length) emptyContent($('map-empty'), !!query || state.filter !== 'all' || !!focusId);
    const world = $('world');
    const activeId = document.activeElement?.dataset?.node;
    world.replaceChildren(); world.setAttribute('transform', `translate(${camera.x} ${camera.y}) scale(${camera.k})`);
    const visibleIds = new Set(filtered.nodes.filter(node => {
      const p = positions.get(node.id), x = p.x * camera.k + camera.x, y = p.y * camera.k + camera.y;
      return x > -150 && y > -100 && x < size.width + 150 && y < size.height + 100;
    }).map(n => n.id));
    for (const edge of filtered.edges) {
      const a = positions.get(edge.from), b = positions.get(edge.to);
      // Cull only offscreen geometry, never truncate the graph's data.
      if (!visibleIds.has(edge.from) && !visibleIds.has(edge.to)) {
        const left = Math.min(a.x, b.x) * camera.k + camera.x, right = Math.max(a.x, b.x) * camera.k + camera.x;
        const top = Math.min(a.y, b.y) * camera.k + camera.y, bottom = Math.max(a.y, b.y) * camera.k + camera.y;
        if (right < 0 || left > size.width || bottom < 0 || top > size.height) continue;
      }
      const highlight = selection === edge.from || selection === edge.to;
      world.append(svgEl('line', { x1: a.x, y1: a.y, x2: b.x, y2: b.y, class: `edge ${edge.kind}${highlight ? ' highlight' : ''}` }));
      if (edge.label && highlight && camera.k > .35) world.append(svgEl('text', { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 - 7, class: 'edge-label' }, short(edge.label, 24)));
    }
    let index = 0;
    for (const node of filtered.nodes) {
      if (!visibleIds.has(node.id)) continue;
      const p = positions.get(node.id), colors = A.palette(scene(node)), radius = node.kind === 'paper' ? 30 : ['note', 'note-ref'].includes(node.kind) ? 26 : 22;
      const g = svgEl('g', { transform: `translate(${p.x} ${p.y})`, class: `node ${node.kind}${node.done ? ' done' : ''}${selection === node.id ? ' selected' : ''}`, role: 'button', tabindex: '0', 'data-node': node.id, 'aria-label': nodeTitle(node) });
      g.append(svgEl('title', {}, nodeTitle(node)), svgEl('circle', { r: radius + 8, class: 'halo' }));
      g.append(svgEl('circle', { r: radius, fill: node.kind === 'paper' ? 'var(--wash)' : colors[0], stroke: node.kind === 'paper' ? 'var(--accent)' : colors[2], 'stroke-width': 1.6 }));
      if (node.kind === 'todo' && camera.k > .3) {
        const clipId = `star-clip-${index++}`, clip = svgEl('clipPath', { id: clipId }); clip.append(svgEl('circle', { r: radius - 1 })); g.append(clip);
        g.append(svgEl('image', { href: image(node), x: -radius, y: -radius, width: radius * 2, height: radius * 2, preserveAspectRatio: 'xMidYMid slice', 'clip-path': `url(#${clipId})` }));
        if (node.done) g.append(svgEl('text', { y: 1, class: 'node-symbol', fill: colors[1] }, '✓'));
      } else g.append(svgEl('text', { y: 1, class: 'node-symbol', fill: colors[1] }, ['note', 'note-ref'].includes(node.kind) ? '✦' : node.kind === 'paper' ? '▤' : node.kind === 'missing' ? '?' : node.done ? '✓' : '·'));
      if (camera.k > .22 || selection === node.id) g.append(svgEl('text', { y: radius + 21, class: 'node-label', style: `font-size:${Math.max(12, 10 / camera.k)}px` }, short(nodeTitle(node))));
      world.append(g);
      if (activeId === node.id) g.focus({ preventScroll: true });
    }
  }
  function localPoint(event) { const r = $('canvas').getBoundingClientRect(); return { x: event.clientX - r.left, y: event.clientY - r.top }; }
  function cancelDrag() {
    if (!drag) return;
    const pointer = drag.pointer; drag = null; rebuild();
    if ($('canvas')) { $('canvas').classList.remove('dragging'); if ($('canvas').hasPointerCapture(pointer)) $('canvas').releasePointerCapture(pointer); }
    schedulePaint();
  }
  function bindCanvas() {
    const canvas = $('canvas');
    canvas.addEventListener('pointerdown', e => {
      if (e.button !== 0 || drag) return;
      const node = e.target.closest('[data-node]');
      if (node) node.focus({ preventScroll: true }); else canvas.focus({ preventScroll: true });
      drag = { pointer: e.pointerId, x: e.clientX, y: e.clientY, key: node?.dataset.node, camera: { ...camera }, pos: node ? { ...positions.get(node.dataset.node) } : null, moved: false, generation };

    });
    canvas.addEventListener('pointermove', e => {
      if (!drag || drag.pointer !== e.pointerId) return;
      const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      if (!drag.moved && Math.hypot(dx, dy) < 4) return;
      if (!canvas.hasPointerCapture(e.pointerId)) canvas.setPointerCapture(e.pointerId);
      drag.moved = true; canvas.classList.add('dragging');
      if (drag.key) positions.set(drag.key, { x: drag.pos.x + dx / camera.k, y: drag.pos.y + dy / camera.k });
      else camera = { ...drag.camera, x: drag.camera.x + dx, y: drag.camera.y + dy };
      schedulePaint();
    });
    canvas.addEventListener('pointerup', e => {
      if (!drag || drag.pointer !== e.pointerId) return;
      const completed = drag, position = completed.key ? positions.get(completed.key) : null;
      drag = null; canvas.classList.remove('dragging');
      if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
      if (completed.moved) {
        suppressClick = true; setTimeout(() => { suppressClick = false; }, 80);
        if (completed.key && completed.generation === generation) {
          if (!commit({ ...state, positions: { ...state.positions, [completed.key]: position } })) { rebuild(); schedulePaint(); }
        }
      }
    });
    canvas.addEventListener('pointercancel', cancelDrag);
    canvas.addEventListener('pointerleave', () => { if (drag && !canvas.hasPointerCapture(drag.pointer)) cancelDrag(); });
    canvas.addEventListener('lostpointercapture', cancelDrag);
    canvas.addEventListener('click', e => {
      if (suppressClick) return;
      const node = e.target.closest('[data-node]');
      if (!node) { selection = null; render(); return; }
      const key = node.dataset.node;
      if (e.shiftKey && selection && selection !== key) commit(C.setLink(state, selection, key));
      else select(key);
    });
    canvas.addEventListener('dblclick', e => {
      if (e.target.closest('[data-node]')) return;
      const p = localPoint(e); editNote(null, { x: (p.x - camera.x) / camera.k, y: (p.y - camera.y) / camera.k });
    });
    canvas.addEventListener('wheel', e => {
      e.preventDefault(); const p = localPoint(e);
      camera = C.zoom(camera, Math.exp(-e.deltaY * (e.deltaMode === 1 ? .04 : .0015)), p.x, p.y); schedulePaint();
    }, { passive: false });
  }
  function closeDialog() {
    if (dialog.open) dialog.close(); claims();
    if (lastFocused?.isConnected) lastFocused.focus({ preventScroll: true });
  }
  function openForm(titleKey, build, submit, submitKey = 'save', danger = false) {
    if (mini || blocked) return;
    if (dialog.open) dialog.close();
    lastFocused = document.activeElement;
    dialog.replaceChildren();
    const form = el('form'), head = el('div', undefined, 'dialog-head'), area = el('div'), actions = el('div', undefined, 'dialog-actions');
    head.append(el('h2', t(titleKey)), button('×', closeDialog, 'icon')); head.lastChild.setAttribute('aria-label', t('close'));
    const submitButton = el('button', t(submitKey), danger ? 'danger' : 'primary'); submitButton.type = 'submit';
    actions.append(button(t('cancel'), closeDialog, 'secondary'), submitButton);
    form.append(head, area, actions); dialog.append(form); build(area);
    form.addEventListener('submit', async e => {
      e.preventDefault(); if (submitButton.disabled || !form.reportValidity()) return;
      submitButton.disabled = true;
      try { if (await submit(area)) { if (dialog.contains(form)) closeDialog(); } }
      catch (error) { problem(error); }
      finally { submitButton.disabled = false; }
    });
    dialog.showModal(); claims();
  }
  function confirmDialog(title, body, action, danger = false) {
    openForm(title, area => area.append(el('p', t(body))), action, 'save', danger);
  }
  function field(area, caption, tag = 'input', value = '') {
    const label = el('label', undefined, 'field'); label.append(el('span', t(caption)));
    const input = el(tag); input.value = value; label.append(input); area.append(label); return input;
  }
  async function chooseSources() {
    await queue.request();
    if (stale) { toast('stale'); return; }
    const choices = new Map();
    openForm('sources', area => {
      area.append(el('p', t('sourceHint'), 'small'));
      if (!papers.length) area.append(el('p', t('noPaper')));
      const list = el('div', undefined, 'source-list');
      const available = papers.map(p => ({ id: p.id, title: p.title || t('untitled') }));
      state.sources.filter(id => !papers.some(p => p.id === id)).forEach(id => available.push({ id, title: `${t('unavailable')} · ${short(id, 12)}` }));
      available.forEach(paper => {
        const label = el('label', undefined, 'source-option'), input = el('input'); input.type = 'checkbox'; input.checked = state.sources.includes(paper.id);
        choices.set(paper.id, input); label.append(input, el('span', paper.title)); list.append(label);
      });
      area.append(list);
    }, () => {
      const sources = [...choices].filter(([, input]) => input.checked).map(([key]) => key);
      if (!commit({ ...state, sources })) return false;
      fitNeeded = true; queue.request(); return true;
    });
  }
  function editNote(node = null, point = null) {
    let title, body;
    const baseline = node ? JSON.stringify(state.notes.find(n => n.id === node.id)) : null;
    openForm(node ? 'edit' : 'newNote', area => {
      title = field(area, 'title', 'input', node?.title || ''); title.required = true;
      body = field(area, 'body', 'textarea', node?.body || '');
    }, () => {
      if (!title.value.trim()) { title.focus(); return false; }
      if (node && baseline !== JSON.stringify(state.notes.find(n => n.id === node.id))) { toast('conflict'); return false; }
      const id = node?.id || `n:${crypto.randomUUID()}`;
      const note = { id, title: title.value.trim(), body: body.value };
      const next = { ...state, notes: node ? state.notes.map(n => n.id === id ? note : n) : [...state.notes, note] };
      if (point) next.positions = { ...state.positions, [id]: point };
      if (!commit(next)) return false;
      selection = id; if (!point && !node) fitNeeded = true; render(); return true;
    });
  }
  function newTodo() {
    if (stale) { toast('stale'); return; }
    const targets = papers.filter(p => p.type === 'todo' && state.sources.includes(p.id));
    if (!targets.length) { chooseSources(); return; }
    let paper, text;
    openForm('newTodo', area => {
      paper = field(area, 'papers', 'select');
      targets.forEach(p => { const option = el('option', p.title || t('untitled')); option.value = p.id; paper.append(option); });
      text = field(area, 'title'); text.required = true;
    }, async () => {
      if (!text.value.trim()) { text.focus(); return false; }
      return mutate('append', 'todos.append', { paperId: paper.value, todos: [{ text: text.value.trim() }] });
    });
  }
  function editTodo(node) {
    let text;
    const baseline = node.todo.text;
    openForm('edit', area => { text = field(area, 'title', 'textarea', baseline); text.required = true; }, async () => {
      if (!text.value.trim()) { text.focus(); return false; }
      await queue.request();
      const latest = model.byId.get(node.id);
      if (stale || latest?.kind !== 'todo' || latest.todo.text !== baseline) { toast('conflict'); return false; }
      return mutate(node.id, 'todos.update', { paperId: node.todo.paperId, todoId: node.todo.id, text: text.value });
    });
  }
  function editLink(from) {
    const candidates = model.nodes.filter(n => n.id !== from);
    if (!candidates.length) { toast('noTarget'); return; }
    let target, label;
    openForm('link', area => {
      target = field(area, 'target', 'select');
      candidates.forEach(node => { const option = el('option', nodeTitle(node)); option.value = node.id; target.append(option); });
      label = field(area, 'relation');
    }, () => {
      if (!model.byId.has(from) || !model.byId.has(target.value)) { toast('conflict'); return false; }
      return commit(C.setLink(state, from, target.value, label.value));
    });
  }
  async function mutate(key, method, params) {
    if (!ready || blocked || mini || stale || pending.has(key)) return false;
    const restoreTaskFocus = document.activeElement?.dataset.action === 'completion' && document.activeElement?.closest('[data-card]')?.dataset.card === key;
    pending.add(key); render();
    try {
      await host.workspace.request(method, params);
      await queue.request();
      return true;
    } catch (error) {
      stale = true;
      toast('operationFailed', error?.code || error?.message || '');
      // A timeout can mean "committed, acknowledgement lost". Read only; never replay a mutation.
      await queue.request();
      return false;
    } finally {
      pending.delete(key); render();
      if (restoreTaskFocus && document.activeElement === document.body) {
        const card = [...document.querySelectorAll('[data-card]')].find(card => card.dataset.card === key);
        card?.querySelector('[data-action=completion]')?.focus({ preventScroll: true });
      }
    }
  }
  function loadImage(source) {
    return new Promise((resolve, reject) => {
      const img = new Image(); img.onload = () => resolve(img); img.onerror = () => reject(new Error('invalidImage')); img.src = source;
    });
  }
  async function compressImage(file) {
    if (!file || !/^image\/(png|jpeg|webp|gif)$/.test(file.type)) throw new Error('invalidImage');
    if (file.size > 20 * 1024 * 1024) throw new Error('imageTooLarge');
    const url = URL.createObjectURL(file);
    try {
      const img = await loadImage(url);
      if (!img.naturalWidth || img.naturalWidth * img.naturalHeight > 32e6) throw new Error('imageTooLarge');
      const canvas = document.createElement('canvas');
      let ratio = Math.min(1, 1024 / Math.max(img.naturalWidth, img.naturalHeight));
      // Covers need thumbnail quality, not a second full-resolution photo library.
      for (let attempt = 0; attempt < 5; attempt++) {
        canvas.width = Math.max(1, Math.round(img.naturalWidth * ratio)); canvas.height = Math.max(1, Math.round(img.naturalHeight * ratio));
        canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);
        const data = canvas.toDataURL('image/webp', .82);
        if (C.bytes(data) <= 512 * 1024) return data;
        ratio *= .7;
      }
      throw new Error('imageTooLarge');
    } finally { URL.revokeObjectURL(url); }
  }
  async function attachImage(file, target) {
    const node = model.byId.get(target);
    if (!node || !['note', 'todo', 'note-ref'].includes(node.kind)) { toast('selectedImage'); return; }
    const job = ++imageJob, version = generation;
    try {
      const data = await compressImage(file);
      if (job !== imageJob || version !== generation || !model.byId.has(target)) return;
      commit({ ...state, covers: { ...state.covers, [target]: { scene: scene(model.byId.get(target)), image: data } } });
    } catch (error) { problem(error); }
  }
  function download(content, type, name) {
    const blob = content instanceof Blob ? content : new Blob([content], { type });
    const url = URL.createObjectURL(blob), link = el('a'); link.href = url; link.download = name;
    document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 60000);
  }
  function downloadJson(value) {
    download(JSON.stringify({ format: 'papertodo.starpaper', version: 1, state: value }), 'application/json', 'Starpaper-backup.json');
  }
  async function importBackup(file) {
    if (!file) return;
    try {
      if (file.size > C.MAX_BYTES * 2) throw new Error('capacity'); // pretty-printed export can exceed compact JSON size
      const value = JSON.parse(await file.text());
      if (value.format !== 'papertodo.starpaper' || value.version !== 1) throw new Error('stateVersion');
      const imported = C.validate(value.state);
      confirmDialog('import', 'importHint', () => {
        if (!commit(imported)) return false;
        imageJob++; selection = null; focusId = null; query = ''; $('search').value = ''; fitNeeded = true;
        queue.request(); render(); return true;
      });
    } catch (error) { problem(error); }
  }
  function exportMap() {
    const filtered = C.visibleGraph(model, state.filter, query, focusId);
    if (!filtered.nodes.length) { toast('noMatch'); return; }
    const xs = filtered.nodes.map(n => positions.get(n.id).x), ys = filtered.nodes.map(n => positions.get(n.id).y);
    const x = xs.reduce((a, b) => Math.min(a, b), Infinity) - 140, y = ys.reduce((a, b) => Math.min(a, b), Infinity) - 100;
    const width = xs.reduce((a, b) => Math.max(a, b), -Infinity) - x + 140, height = ys.reduce((a, b) => Math.max(a, b), -Infinity) - y + 130;
    const svg = svgEl('svg', { xmlns: NS, viewBox: `${x} ${y} ${width} ${height}`, width: Math.ceil(width), height: Math.ceil(height) });
    const style = getComputedStyle(document.documentElement), paper = style.getPropertyValue('--paper').trim(), text = style.getPropertyValue('--text').trim(), accent = style.getPropertyValue('--accent').trim();
    svg.append(svgEl('title', {}, t('map')), svgEl('rect', { x, y, width, height, fill: paper }));
    filtered.edges.forEach(edge => {
      const a = positions.get(edge.from), b = positions.get(edge.to);
      const line = svgEl('line', { x1: a.x, y1: a.y, x2: b.x, y2: b.y, stroke: accent, 'stroke-opacity': '.45', 'stroke-width': 1.5 });
      if (edge.kind !== 'manual') line.setAttribute('stroke-dasharray', '4 5');
      svg.append(line);
      if (edge.label) svg.append(svgEl('text', { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 - 5, fill: text, 'font-size': 11, 'text-anchor': 'middle' }, edge.label));
    });
    filtered.nodes.forEach(node => {
      const p = positions.get(node.id), group = svgEl('g'); group.append(svgEl('title', {}, nodeTitle(node)));
      group.append(svgEl('circle', { cx: p.x, cy: p.y, r: 23, fill: A.palette(scene(node))[0], stroke: accent, 'stroke-width': 1.5 }));
      group.append(svgEl('text', { x: p.x, y: p.y + 6, 'text-anchor': 'middle', 'font-size': 18, fill: accent }, node.done ? '✓' : node.kind === 'paper' ? '▤' : '✦'));
      group.append(svgEl('text', { x: p.x, y: p.y + 47, 'text-anchor': 'middle', 'font-family': 'sans-serif', 'font-size': 13, fill: text }, short(nodeTitle(node), 34)));
      svg.append(group);
    });
    download(new XMLSerializer().serializeToString(svg), 'image/svg+xml', 'Starpaper-map.svg');
  }
  async function exportCard(node) {
    const source = image(node), title = nodeTitle(node), subtitle = node.todo?.paperTitle || t('knowledge');
    const img = await loadImage(source), canvas = document.createElement('canvas'); canvas.width = 960; canvas.height = 1120;
    const ctx = canvas.getContext('2d'), colors = A.palette(scene(node));
    ctx.fillStyle = '#fcfaf5'; ctx.fillRect(0, 0, 960, 1120);
    const ratio = Math.max(960 / img.width, 630 / img.height), w = img.width * ratio, h = img.height * ratio;
    ctx.save(); ctx.beginPath(); ctx.rect(0, 0, 960, 630); ctx.clip(); ctx.drawImage(img, (960 - w) / 2, (630 - h) / 2, w, h); ctx.restore();
    ctx.fillStyle = colors[1]; ctx.font = '24px sans-serif'; ctx.fillText(short(subtitle, 38), 70, 698);
    ctx.fillStyle = '#343c38'; ctx.font = '38px sans-serif';
    const words = typeof Intl.Segmenter === 'function' ? [...new Intl.Segmenter(locale, { granularity: 'grapheme' }).segment(title)].map(s => s.segment) : [...title];
    const lines = []; let line = '';
    words.forEach(char => {
      if (char === '\n' || ctx.measureText(line + char).width > 820) { lines.push(line); line = char === '\n' ? '' : char; }
      else line += char;
    });
    if (line) lines.push(line);
    lines.slice(0, 5).forEach((value, i) => ctx.fillText(i === 4 && lines.length > 5 ? short(value, Math.max(1, [...value].length - 1)) + '…' : value, 70, 764 + i * 52));
    ctx.fillStyle = colors[1]; ctx.font = '23px sans-serif'; ctx.fillText(node.done ? `✓ ${t('done')}` : '✦ Starpaper', 70, 1054);
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    if (!blob) throw new Error('invalidImage');
    download(blob, 'image/png', 'Starpaper-card.png');
  }

  async function loadWorkspace() {
    // Only paper metadata is read before the user chooses sources. No all-workspace todo scan.
    const sources = [...state.sources];
    const all = await host.workspace.request('papers.list');
    if (!Array.isArray(all) || all.some(p => typeof p.id !== 'string' || typeof p.type !== 'string' || typeof p.title !== 'string')) throw new Error('protocolData');
    const nextPapers = all.filter(p => p.type === 'todo' || (p.type === 'note' && p.bodyProviderId === 'builtin.markdown'));
    const nextTodos = [], nextNotes = [];
    for (const paper of nextPapers.filter(p => sources.includes(p.id))) {
      if (paper.type === 'todo') {
        const items = await host.workspace.request('todos.list', { paperId: paper.id, includeBlank: false });
        if (!Array.isArray(items) || items.some(item => item.paperId !== paper.id || typeof item.id !== 'string' || typeof item.text !== 'string' || typeof item.done !== 'boolean')) throw new Error('protocolData');
        nextTodos.push(...items);
      } else {
        const note = await host.workspace.request('notes.get', { paperId: paper.id });
        if (note && (note.paperId !== paper.id || typeof note.contentAvailable !== 'boolean' || typeof note.content !== 'string')) throw new Error('protocolData');
        if (note) nextNotes.push(note);
      }
    }
    return { papers: nextPapers, todos: nextTodos, sourceNotes: nextNotes };
  }
  function renderMini() {
    const box = el('main', undefined, 'mini'), head = el('div', undefined, 'mini-head');
    head.append(el('strong', '✦ ' + t('name')), el('span', t('local'))); box.append(head);
    const tasks = model.nodes.filter(n => n.kind === 'todo'), open = tasks.filter(n => !n.done);
    const ideas = model.nodes.filter(n => n.kind === 'note' || n.kind === 'note-ref');
    const counts = el('div'); counts.append(el('span', String(open.length), 'mini-count'), el('span', t('open') + ' · ' + ideas.length + ' ' + t('knowledge'), 'small')); box.append(counts);
    const list = (open.length ? open : ideas).slice(0, 3);
    list.forEach(node => { const row = el('div', undefined, 'mini-row'), img = el('img'); img.src = image(node); img.alt = ''; row.append(img, el('span', nodeTitle(node))); box.append(row); });
    if (!list.length) box.append(el('p', t('emptyTitle'), 'small'));
    if (stale) box.append(el('p', t('stale'), 'small'));
    app.replaceChildren(box);
    publishMiniReady();
  }
  function publishMiniReady() {
    if (!mini || miniReady || (!workspaceLoaded && !blocked)) return;
    miniReady = true;
    // The host owns publication/visibility. Signal only after our own first real layout.
    requestAnimationFrame(() => { if (ready) { app.getBoundingClientRect(); host.mini.ready(); } });
  }
  function blockState(error) {
    blocked = true; imageJob++; generation++; cancelDrag();
    queue?.dispose(); unsubscribe?.(); unsubscribe = null; resize?.disconnect();
    cancelAnimationFrame(frame); frame = 0; closeDialog();
    const panel = el('main', undefined, 'empty'); panel.append(el('h2', t('invalidState')), el('p', t(I.rows[error?.message] ? error.message : 'invalidState')));
    if (!mini) panel.append(button(t('export'), () => downloadJson(original), 'primary'));
    app.replaceChildren(panel); mounted = false; publishMiniReady();
  }
  function initialize(message) {
    host = window.papertodo || host;
    if (!host) return;
    queue?.dispose(); unsubscribe?.(); unsubscribe = null; resize?.disconnect();
    generation++; imageJob++; pending.clear(); history.clear();
    ready = true; blocked = false; workspaceLoaded = false; mini = message.surface === 'mini' || host.surface === 'mini';
    visible = message.visible !== false; original = message.state;
    locale = I.language(message.settings?.language, navigator.language); t = I.translator(locale); theme(message.theme);
    try { state = C.validate(message.state); } catch (error) { blockState(error); return; }
    if (!mini) {
      // A provider is only registered after validation. It must never flush a guessed empty state.
      host.registerStateProvider(() => { if (blocked) throw new Error('invalidState'); return state; });
    }
    queue = new C.RefreshQueue(loadWorkspace, snapshot => {
      papers = snapshot.papers; todos = snapshot.todos; sourceNotes = snapshot.sourceNotes; stale = false; workspaceLoaded = true; render();
    }, () => { stale = true; workspaceLoaded = true; render(); });
    if (!mini && host.onHostEvent) {
      unsubscribe = host.onHostEvent(['paper.created', 'paper.changed', 'paper.deleted', 'todo.created', 'todo.changed', 'todo.deleted', 'note.changed'], () => { if (visible && !blocked) queue.request(); }, { excludeOwnOperations: false });
    }
    mounted = false; fitNeeded = true; selection = null; focusId = null; render(); queue.request();
  }
  function onMessage(message) {
    if (!message || typeof message.type !== 'string') return;
    if (message.type === 'initialize') { initialize(message); return; }
    if (!ready) return;
    if (message.type === 'stateChanged') {
      original = message.state;
      if (blocked) return; // Recovery requires reopening the document, not an automatic overwrite.
      try {
        const next = C.validate(message.state);
        if (JSON.stringify(next) === JSON.stringify(state)) return;
        cancelDrag(); state = next; generation++; imageJob++; history.clear(); fitNeeded = true;
        render(); queue.request(); // Never echo a received state back to the host.
      } catch (error) { blockState(error); }
    } else if (message.type === 'settingsChanged') {
      locale = I.language(message.settings?.language, navigator.language); t = I.translator(locale); translate(); render();
    } else if (message.type === 'themeChanged' || message.type === 'typographyChanged') {
      theme(message.theme); render();
    } else if (message.type === 'visibilityChanged') {
      visible = message.visible !== false;
      if (!visible) { cancelDrag(); cancelAnimationFrame(frame); frame = 0; }
      else { render(); if (!blocked) queue.request(); }
    } else if (message.type === 'activated') {
      if (!blocked) queue.request();
    } else if (message.type === 'cancelInteractions' || message.type === 'deactivated') {
      cancelDrag();
    } else if (message.type === 'hostSubscriptionError') {
      stale = true; render(); toast('stale');
    }
  }
  window.addEventListener('papertodo', event => onMessage(event.detail));
  dialog.addEventListener('close', claims);
  dialog.addEventListener('cancel', event => { event.preventDefault(); closeDialog(); });
  function isEditing(target) { return !!target?.closest('input, textarea, select, [contenteditable="true"]'); }
  document.addEventListener('keydown', event => {
    if (!ready || blocked || mini || event.isComposing) return;
    if (event.key === 'Escape') {
      if (dialog.open) { event.preventDefault(); closeDialog(); }
      else if (selection || focusId || drag) { event.preventDefault(); cancelDrag(); selection = null; focusId = null; render(); }
      return;
    }
    if (dialog.open || isEditing(event.target)) return;
    if ((event.ctrlKey || event.metaKey) && ['z', 'y'].includes(event.key.toLowerCase())) {
      event.preventDefault(); undo(event.key.toLowerCase() === 'y' || event.shiftKey); return;
    }
    const node = event.target.closest('#canvas [data-node]');
    if (node && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); select(node.dataset.node); return; }
    if (event.target.id === 'canvas' || node) {
      const delta = { ArrowLeft: [45, 0], ArrowRight: [-45, 0], ArrowUp: [0, 45], ArrowDown: [0, -45] }[event.key];
      if (delta) { event.preventDefault(); camera.x += delta[0]; camera.y += delta[1]; schedulePaint(); }
      else if (event.key.toLowerCase() === 'f') { event.preventDefault(); fitAll(); }
      else if (['+', '=', '-'].includes(event.key)) { event.preventDefault(); camera = C.zoom(camera, event.key === '-' ? .8 : 1.25, size.width / 2, size.height / 2); schedulePaint(); }
    }
  });
  $('image-file').onchange = event => { const file = event.target.files[0]; if (file) attachImage(file, imageTarget); };
  $('backup-file').onchange = event => { const file = event.target.files[0]; if (file) importBackup(file).catch(problem); };
  document.addEventListener('paste', event => {
    if (!ready || blocked || mini || dialog.open || isEditing(event.target)) return;
    const file = [...(event.clipboardData?.items || [])].find(item => item.kind === 'file' && item.type.startsWith('image/'))?.getAsFile();
    if (file) { event.preventDefault(); attachImage(file, selection); }
  });
  document.addEventListener('dragover', event => { if (!mini && event.dataTransfer?.types.includes('Files')) event.preventDefault(); });
  document.addEventListener('drop', event => {
    if (!ready || blocked || mini || dialog.open || !event.dataTransfer?.files.length) return;
    event.preventDefault(); const file = event.dataTransfer.files[0];
    const target = event.target.closest('[data-node]')?.dataset.node || event.target.closest('[data-card]')?.dataset.card || selection;
    attachImage(file, target);
  });
  window.addEventListener('pagehide', () => {
    ready = false; generation++; imageJob++; queue?.dispose(); unsubscribe?.(); resize?.disconnect();
    cancelAnimationFrame(frame); clearTimeout(toastTimer);
  });

  // Explicit, memory-only preview. Production index.html never substitutes fake host data.
  function previewHost() {
    const samplePapers = [
      { id: 'preview-tasks', type: 'todo', title: '让想法落地', bodyProviderId: '' },
      { id: 'preview-notes', type: 'note', title: '设计原则', bodyProviderId: 'builtin.markdown' }
    ];
    const sampleTasks = [
      ['reading', '读完《设计中的设计》', false], ['drawing', '画一张自己的星图', false],
      ['code', '完成插件的第一轮测试', true], ['health', '傍晚去公园散步', false],
      ['travel', '安排一场周末短途旅行', false], ['grow', '给阳台的植物浇水', true]
    ].map(([id, text, done], order) => ({ id, text, done, order, paperId: 'preview-tasks', paperTitle: '让想法落地' }));
    const sampleNote = { paperId: 'preview-notes', paperTitle: '设计原则', contentAvailable: true, content: '纸片优先，即时使用。\n\n从 [[微小行动]] 开始，把想法放进日常，而不是放进另一个管理系统。' };
    let listener = () => {};
    const preview = {
      surface: 'body', saveState() {}, registerStateProvider() {},
      paper: { setHeaderText() {}, setCapsulePresentation() {} }, body: { setInputClaims() {} },
      onHostEvent(types, handler) { listener = handler; return () => { listener = () => {}; }; },
      workspace: { async request(method, args = {}) {
        if (method === 'papers.list') return structuredClone(samplePapers);
        if (method === 'todos.list') return structuredClone(sampleTasks.filter(item => item.paperId === args.paperId));
        if (method === 'notes.get') return structuredClone(sampleNote);
        if (method === 'todos.update') {
          const item = sampleTasks.find(item => item.id === args.todoId && item.paperId === args.paperId);
          if (!item) throw new Error('unavailable');
          if (typeof args.done === 'boolean') item.done = args.done;
          if (typeof args.text === 'string') item.text = args.text;
          listener({ type: 'todo.changed' }); return { paperId: args.paperId, todoId: args.todoId };
        }
        if (method === 'todos.append') {
          const ids = args.todos.map(item => { const id = crypto.randomUUID(); sampleTasks.push({ ...item, id, done: false, order: sampleTasks.length, paperId: args.paperId, paperTitle: '让想法落地' }); return id; });
          listener({ type: 'todo.created' }); return { paperId: args.paperId, todoIds: ids };
        }
        throw new Error('protocolData');
      } }
    };
    const sample = { ...C.blank(), sources: ['preview-tasks', 'preview-notes'], notes: [
      { id: 'n:curiosity', title: '保持好奇', body: '把看到的、想到的，留成一颗星。\n\n通过 [[微小行动]]，让好奇心有一个落点。' },
      { id: 'n:action', title: '微小行动', body: '不等待一个完整计划。\n先做一件今天可以完成的小事。\n\n关联：[[设计原则]]' },
      { id: 'n:garden', title: '想法花园', body: '知识不必排成一列。\n让想法像植物一样，自由生长。' }
    ], links: [
      { from: 'n:curiosity', to: C.todoKey('preview-tasks', 'reading'), label: '输入' },
      { from: 'n:garden', to: C.todoKey('preview-tasks', 'drawing'), label: '创作' },
      { from: 'n:action', to: C.todoKey('preview-tasks', 'health'), label: '今天就做' }
    ] };
    host = preview; initialize({ type: 'initialize', surface: 'body', state: sample, settings: { language: 'zh' } });
  }
  if (demo) previewHost();
  else { app.replaceChildren(el('main', t('waiting'), 'empty')); }
})();
