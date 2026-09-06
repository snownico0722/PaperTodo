/* The website is a simulation. It never calls a native bridge, evaluates scripts,
   uploads demo text, or persists paper content. Only the language preference is saved. */
(() => {
  'use strict';
  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const repo = 'https://github.com/snownico0722/PaperTodo';
  const narrow = matchMedia('(max-width: 640px)');
  const mobileNav = matchMedia('(max-width: 760px)');
  const hover = matchMedia('(hover: hover) and (pointer: fine)');
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  const stage = $('#workspace');
  const playground = $('#playground');
  const papers = { todo: $('#paper-todo'), note: $('#paper-note') };
  const preview = $('#edge-preview');
  const dock = $('#edge-dock');
  const strings = {
    zh: {
      task1: '给今天留一点空白', task2: '把灵感写在旁边的纸上', task3: '完成一件小事就好',
      note: '## 给想法一点空间\n\n今天先做好 **一件事**。\n\n- 留一点空白\n- 写下忽然冒出的灵感\n\n> 不必打开另一个世界。\n\n`Ctrl + W` 折成胶囊',
      todoTitle: '今天，做一点点', noteTitle: '一些灵感',
      edit: '编辑待办', check: '完成或恢复待办', remove: '删除待办', link: '打开关联笔记',
      empty: '还没有待办，写下第一件小事吧。', emptyNote: '笔记还是空的。切到原文，写点什么吧。',
      added: '已添加待办。', removed: '已删除待办。可用撤销恢复。', updated: '待办与预览已同步。',
      undone: '已撤销上一步待办操作。', reset: '演示已重置，页面中的修改已清除。',
      folded: '纸片已收起。悬停胶囊可预览；触屏轻点胶囊可预览。', opened: '纸片已展开。',
      queueHide: '收拢边缘队列', queueShow: '展开边缘队列', foldedCount: '张已收起的纸片',
      hintDesktop: '可编辑、勾选；拖动标题，靠边松手收起。悬停胶囊预览，点击展开。',
      hintMobile: '可编辑、勾选；用上方按钮切换纸片。收起后，轻点边缘胶囊预览。',
      left: '胶囊已移到左边。', right: '胶囊已移到右边。',
      menuOpen: '展开导航', menuClose: '收起导航', start: '开始专注', pause: '暂停', resume: '继续专注',
      restart: '重新开始', timerStarted: '已开始 25 分钟专注计时。', timerResumed: '专注计时已继续。',
      timerPaused: '专注计时已暂停。', timerReset: '专注计时已重置。', timerDone: '这一段专注完成了。休息一下吧。',
      focusFold: '收成胶囊试试', focusOpen: '恢复纸片', focusTime: '展开专注计时，剩余',
      scriptRun: '运行演示', scriptRunning: '模拟运行中…', scriptReplay: '再演示一次',
      scriptDone: '模拟网页已打开；没有执行任何 PowerShell 或访问外部网站。'
    },
    en: {
      task1: 'Leave a little breathing room', task2: 'Write that thought on the note', task3: 'One small thing is enough',
      note: '## A little room for ideas\n\nDo **one thing** well today.\n\n- Leave a little space\n- Catch a passing thought\n\n> No need to open another world.\n\n`Ctrl + W` folds a paper',
      todoTitle: 'A little for today', noteTitle: 'A passing thought',
      edit: 'Edit task', check: 'Complete or restore task', remove: 'Delete task', link: 'Open linked note',
      empty: 'No tasks yet. Write down one little thing.', emptyNote: 'An empty note. Switch to Raw to write something.',
      added: 'Task added.', removed: 'Task deleted. Use Undo to restore it.', updated: 'Task and preview are in sync.',
      undone: 'Last task action undone.', reset: 'Demo reset. Changes on this page were cleared.',
      folded: 'Paper folded. Hover to preview, or tap the capsule on a touchscreen.', opened: 'Paper opened.',
      queueHide: 'Gather edge queue', queueShow: 'Expand edge queue', foldedCount: 'folded papers',
      hintDesktop: 'Edit and check tasks. Drag a title to an edge to fold. Hover to preview; click to open.',
      hintMobile: 'Edit and check tasks. Switch papers above. After folding, tap an edge capsule to preview.',
      left: 'Capsules moved to the left.', right: 'Capsules moved to the right.',
      menuOpen: 'Open navigation', menuClose: 'Close navigation', start: 'Start', pause: 'Pause', resume: 'Resume',
      restart: 'Start again', timerStarted: '25-minute focus timer started.', timerResumed: 'Focus timer resumed.',
      timerPaused: 'Focus timer paused.', timerReset: 'Focus timer reset.', timerDone: 'Focus session complete. Take a break.',
      focusFold: 'Fold into a capsule', focusOpen: 'Restore paper', focusTime: 'Open focus timer, time remaining',
      scriptRun: 'Run simulation', scriptRunning: 'Simulating…', scriptReplay: 'Replay demo',
      scriptDone: 'Simulated page opened. No PowerShell was executed and no external site was accessed.'
    }
  };
  let language = 'zh';
  try {
    const queryLanguage = new URL(location.href).searchParams.get('lang');
    const saved = localStorage.getItem('papertodo.website.lang');
    const candidate = queryLanguage === 'en' || queryLanguage === 'zh' ? queryLanguage : saved;
    if (candidate === 'en') language = 'en';
  } catch {
    // A blocked storage API must not break shared ?lang=en links.
    if (new URL(location.href).searchParams.get('lang') === 'en') language = 'en';
  }
  const t = key => strings[language][key];
  const staticText = $$('[data-en]').map(element => ({
    element, zh: [...element.childNodes].map(node => node.cloneNode(true)), en: element.dataset.en
  }));
  const staticAttributes = [
    ...$$('[data-en-label]').map(element => ({ element, attr: 'aria-label', zh: element.getAttribute('aria-label'), en: element.dataset.enLabel })),
    ...$$('[data-en-placeholder]').map(element => ({ element, attr: 'placeholder', zh: element.getAttribute('placeholder'), en: element.dataset.enPlaceholder }))
  ];
  const initialTitle = document.title;
  const initialDescription = $('meta[name="description"]').content;
  const initialState = () => ({
    tasks: [
      { id: 1, key: 'task1', custom: false, text: '', done: true, linked: false },
      { id: 2, key: 'task2', custom: false, text: '', done: false, linked: true },
      { id: 3, key: 'task3', custom: false, text: '', done: false, linked: false }
    ],
    nextId: 4, noteText: '', noteCustom: false, mode: 'enhanced',
    folded: { todo: false, note: false }, active: 'todo', scene: 'todo',
    positions: { todo: null, note: null }, side: 'right', queueOpen: true, preview: null
  });
  let state = initialState();
  let undoStack = [];
  let editSnapshotTaken = false;
  let closeTimer = 0;
  let suppressPreviewFocus = false;
  let lastPointerType = 'mouse';
  let drag = null;
  const duration = 25 * 60 * 1000;
  let remaining = duration;
  let deadline = 0;
  let timerRunning = false;
  let timerHandle = 0;
  let focusFolded = false;
  let scriptHandle = 0;
  let scriptHasRun = false;
  let scriptRunning = false;
  const taskText = task => task.custom ? task.text : t(task.key);
  const noteText = () => state.noteCustom ? state.noteText : t('note');
  const entranceAnimations = new WeakMap();
  function enter(element, distance = 7) {
    const previous = entranceAnimations.get(element);
    if (previous) previous.cancel();
    if (reducedMotion.matches || !element.animate || element.hidden) return;
    const animation = element.animate([
      { opacity: 0, transform: `translateY(${distance}px)` },
      { opacity: 1, transform: 'translateY(0)' }
    ], { duration: 180, easing: 'cubic-bezier(.2,.7,.2,1)' });
    entranceAnimations.set(element, animation);
  }
  const announce = message => { $('#demo-status').textContent = message; };
  const cloneTasks = () => state.tasks.map(task => ({ ...task }));
  function rememberTasks() {
    undoStack.push(cloneTasks());
    if (undoStack.length > 30) undoStack.shift();
    $('#undo-todo').disabled = false;
  }
  function captureFocus(root) {
    const element = document.activeElement;
    if (!root.contains(element) || !element.dataset.focusKey) return null;
    return { key: element.dataset.focusKey, start: element.selectionStart, end: element.selectionEnd };
  }
  function restoreFocus(root, record) {
    if (!record) return;
    const element = $('[data-focus-key="' + record.key + '"]', root);
    if (!element) return;
    element.focus({ preventScroll: true });
    if (element instanceof HTMLTextAreaElement && record.start !== null) element.setSelectionRange(record.start, record.end);
  }
  function sizeEditor(editor) {
    editor.style.height = '30px';
    editor.style.height = Math.min(70, Math.max(30, editor.scrollHeight)) + 'px';
  }
  function renderTodos(focusId) {
    const list = $('#todo-list');
    const focus = captureFocus(list);
    const scroll = list.scrollTop;
    const fragment = document.createDocumentFragment();
    for (const task of state.tasks) {
      const row = document.createElement('div');
      row.className = 'todo-row' + (task.done ? ' is-done' : '');
      const label = document.createElement('label');
      label.className = 'todo-check';
      const checkbox = document.createElement('input');
      checkbox.type = 'checkbox';
      checkbox.checked = task.done;
      checkbox.dataset.todoCheck = task.id;
      checkbox.dataset.focusKey = 'todo-check-' + task.id;
      checkbox.setAttribute('aria-label', t('check') + ': ' + taskText(task));
      label.append(checkbox);
      const editor = document.createElement('textarea');
      editor.rows = 1;
      editor.value = taskText(task);
      editor.dataset.todoEdit = task.id;
      editor.dataset.focusKey = 'todo-edit-' + task.id;
      editor.setAttribute('aria-label', t('edit') + ' ' + (state.tasks.indexOf(task) + 1));
      const action = document.createElement('button');
      action.type = 'button';
      action.textContent = task.linked ? '↗' : '×';
      action.setAttribute('aria-label', task.linked ? t('link') : t('remove') + ': ' + taskText(task));
      action.dataset[task.linked ? 'openNote' : 'removeTodo'] = task.id;
      action.dataset.focusKey = 'todo-action-' + task.id;
      row.append(label, editor, action);
      fragment.append(row);
    }
    if (!state.tasks.length) {
      const empty = document.createElement('p');
      empty.className = 'small-note';
      empty.textContent = t('empty');
      fragment.append(empty);
    }
    list.replaceChildren(fragment);
    $$('textarea', list).forEach(sizeEditor);
    list.scrollTop = scroll;
    $('#todo-count').textContent = state.tasks.filter(task => task.done).length + ' / ' + state.tasks.length;
    $('#undo-todo').disabled = undoStack.length === 0;
    if (focusId !== undefined) {
      const target = $('[data-todo-edit="' + focusId + '"]', list);
      if (target) { target.focus({ preventScroll: true }); target.scrollIntoView({ block: 'nearest', behavior: 'auto' }); }
      else $('#new-todo').focus({ preventScroll: true });
    } else restoreFocus(list, focus);
    if (state.preview === 'todo') renderPreviewContent();
    requestAnimationFrame(positionPapers);
  }
  function toggleTask(id, done) {
    const task = state.tasks.find(item => item.id === id);
    if (!task) return;
    rememberTasks();
    task.done = done;
    renderTodos();
    announce(t('updated'));
  }
  function removeTask(id) {
    const index = state.tasks.findIndex(task => task.id === id);
    if (index < 0) return;
    rememberTasks();
    state.tasks.splice(index, 1);
    const next = state.tasks[Math.max(0, index - 1)];
    renderTodos(next ? next.id : -1);
    announce(t('removed'));
  }
  $('#todo-list').addEventListener('change', event => {
    if (event.target.matches('[data-todo-check]')) toggleTask(Number(event.target.dataset.todoCheck), event.target.checked);
  });
  $('#todo-list').addEventListener('focusin', event => {
    if (event.target.matches('[data-todo-edit]')) editSnapshotTaken = false;
  });
  $('#todo-list').addEventListener('input', event => {
    const editor = event.target;
    if (!editor.matches('[data-todo-edit]')) return;
    const task = state.tasks.find(item => item.id === Number(editor.dataset.todoEdit));
    if (!editSnapshotTaken) { rememberTasks(); editSnapshotTaken = true; }
    task.custom = true;
    task.text = editor.value;
    sizeEditor(editor);
    $('[data-todo-check="' + task.id + '"]').setAttribute('aria-label', t('check') + ': ' + task.text);
    if (state.preview === 'todo') renderPreviewContent();
  });
  $('#todo-list').addEventListener('click', event => {
    const action = event.target.closest('button');
    if (!action) return;
    if (action.hasAttribute('data-open-note')) openPaper('note');
    if (action.hasAttribute('data-remove-todo')) removeTask(Number(action.dataset.removeTodo));
  });
  $('#todo-list').addEventListener('keydown', event => {
    const editor = event.target;
    if (!editor.matches('[data-todo-edit]') || event.isComposing || event.keyCode === 229 || event.ctrlKey || event.metaKey || event.altKey) return;
    const id = Number(editor.dataset.todoEdit);
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      rememberTasks();
      const next = { id: state.nextId++, text: '', custom: true, done: false, linked: false };
      state.tasks.splice(state.tasks.findIndex(task => task.id === id) + 1, 0, next);
      renderTodos(next.id);
    } else if (event.key === 'Backspace' && editor.value === '') {
      event.preventDefault();
      removeTask(id);
    }
  });
  $('#add-form').addEventListener('submit', event => {
    event.preventDefault();
    const field = $('#new-todo');
    const value = field.value.trim();
    if (!value) return;
    rememberTasks();
    const task = { id: state.nextId++, text: value, custom: true, done: false, linked: false };
    state.tasks.push(task);
    field.value = '';
    renderTodos(task.id);
    announce(t('added'));
  });
  $('#undo-todo').addEventListener('click', () => {
    if (!undoStack.length) return;
    state.tasks = undoStack.pop();
    renderTodos();
    announce(t('undone'));
  });
  // This intentionally small renderer is a demo, not the application's Markdown
  // engine. User text becomes text nodes, never HTML, URLs, or executable code.
  function appendInline(parent, text) {
    const pattern = /\*\*([^*\n]+)\*\*|`([^`\n]+)`/g;
    let offset = 0;
    for (const match of text.matchAll(pattern)) {
      parent.append(document.createTextNode(text.slice(offset, match.index)));
      const element = document.createElement(match[1] !== undefined ? 'strong' : 'code');
      element.textContent = match[1] !== undefined ? match[1] : match[2];
      parent.append(element);
      offset = match.index + match[0].length;
    }
    parent.append(document.createTextNode(text.slice(offset)));
  }
  function markdownInto(root, text, basic = false) {
    const fragment = document.createDocumentFragment();
    if (basic) {
      for (const line of text.split('\n')) {
        const pattern = /^(?:#{1,6}\s|>\s?|-\s)|\*\*|`/g;
        let offset = 0;
        for (const match of line.matchAll(pattern)) {
          fragment.append(document.createTextNode(line.slice(offset, match.index)));
          const marker = document.createElement('span');
          marker.className = 'syntax-marker';
          marker.textContent = match[0];
          fragment.append(marker);
          offset = match.index + match[0].length;
        }
        fragment.append(document.createTextNode(line.slice(offset) + '\n'));
      }
    } else {
      let list = null;
      for (const line of text.split('\n')) {
        if (!line.trim()) { list = null; continue; }
        const heading = line.match(/^(#{1,6})\s+(.*)$/);
        const item = line.match(/^\s*-\s+(.*)$/);
        const quote = line.match(/^>\s?(.*)$/);
        if (item) {
          if (!list) { list = document.createElement('ul'); fragment.append(list); }
          const li = document.createElement('li');
          appendInline(li, item[1]);
          list.append(li);
        } else {
          list = null;
          const element = document.createElement(heading ? (heading[1].length <= 2 ? 'h3' : 'h4') : quote ? 'blockquote' : /^-{3,}\s*$/.test(line) ? 'hr' : 'p');
          if (element.tagName !== 'HR') appendInline(element, heading ? heading[2] : quote ? quote[1] : line);
          fragment.append(element);
        }
      }
      if (!text.trim()) {
        const empty = document.createElement('p');
        empty.textContent = t('emptyNote');
        fragment.append(empty);
      }
    }
    root.replaceChildren(fragment);
  }
  function renderNote() {
    const text = noteText();
    if ($('#note-editor').value !== text) $('#note-editor').value = text;
    markdownInto($('#note-basic'), text, true);
    markdownInto($('#panel-enhanced'), text);
    if (state.preview === 'note') renderPreviewContent();
  }
  function setMode(mode, focus = false) {
    state.mode = mode;
    $$('[data-mode]').forEach(button => {
      const active = button.dataset.mode === mode;
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
      $('#panel-' + button.dataset.mode).hidden = !active;
      if (active && focus) button.focus();
    });
  }
  $('#note-editor').addEventListener('input', event => {
    state.noteCustom = true;
    state.noteText = event.target.value;
    renderNote();
  });
  const modeButtons = $$('[data-mode]');
  modeButtons.forEach((button, index) => {
    button.addEventListener('click', () => setMode(button.dataset.mode));
    button.addEventListener('keydown', event => {
      const next = event.key === 'ArrowRight' ? (index + 1) % 3 : event.key === 'ArrowLeft' ? (index + 2) % 3 : event.key === 'Home' ? 0 : event.key === 'End' ? 2 : -1;
      if (next < 0) return;
      event.preventDefault();
      setMode(modeButtons[next].dataset.mode, true);
    });
  });
  function focusCapsule(id) {
    const button = $('[data-capsule="' + id + '"]');
    if (button.hidden || !state.queueOpen) return;
    suppressPreviewFocus = true;
    button.focus({ preventScroll: true });
    suppressPreviewFocus = false;
  }
  function closePreview(restore = false) {
    clearTimeout(closeTimer);
    const id = state.preview;
    state.preview = null;
    preview.hidden = true;
    $$('[data-capsule]').forEach(button => button.setAttribute('aria-expanded', 'false'));
    if (restore && id) focusCapsule(id);
  }
  function renderPreviewContent() {
    if (!state.preview) return;
    const body = $('#preview-body');
    const focus = captureFocus(body);
    const scroll = body.scrollTop;
    $('#preview-title').textContent = t(state.preview === 'todo' ? 'todoTitle' : 'noteTitle');
    if (state.preview === 'note') {
      body.classList.add('markdown');
      markdownInto(body, noteText());
    } else {
      body.classList.remove('markdown');
      const fragment = document.createDocumentFragment();
      state.tasks.forEach(task => {
        const label = document.createElement('label');
        label.className = 'preview-task';
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.checked = task.done;
        checkbox.dataset.previewCheck = task.id;
        checkbox.dataset.focusKey = 'preview-check-' + task.id;
        const text = document.createElement('span');
        text.textContent = taskText(task) || t('edit');
        label.append(checkbox, text);
        fragment.append(label);
      });
      if (!state.tasks.length) {
        const empty = document.createElement('p');
        empty.textContent = t('empty');
        fragment.append(empty);
      }
      body.replaceChildren(fragment);
    }
    body.scrollTop = scroll;
    restoreFocus(body, focus);
  }
  function showPreview(id) {
    if (drag || !state.folded[id] || !state.queueOpen) return;
    clearTimeout(closeTimer);
    const changed = state.preview !== id || preview.hidden;
    state.preview = id;
    renderPreviewContent();
    preview.hidden = false;
    if (changed) enter(preview, 4);
    $$('[data-capsule]').forEach(button => button.setAttribute('aria-expanded', String(button.dataset.capsule === id)));
  }
  function schedulePreviewClose() {
    clearTimeout(closeTimer);
    closeTimer = setTimeout(() => {
      const focused = dock.contains(document.activeElement) || preview.contains(document.activeElement);
      const hovered = hover.matches && (dock.matches(':hover') || preview.matches(':hover'));
      if (!focused && !hovered) closePreview();
    }, 180);
  }
  $('#preview-body').addEventListener('change', event => {
    if (event.target.matches('[data-preview-check]')) toggleTask(Number(event.target.dataset.previewCheck), event.target.checked);
  });
  $$('[data-capsule]').forEach(button => {
    const id = button.dataset.capsule;
    button.addEventListener('pointerdown', event => { lastPointerType = event.pointerType; });
    button.addEventListener('pointerenter', event => { if (event.pointerType === 'mouse' && hover.matches) showPreview(id); });
    button.addEventListener('focus', () => { if (!suppressPreviewFocus) showPreview(id); });
    button.addEventListener('click', event => {
      if (event.detail === 0 || (lastPointerType !== 'touch' && hover.matches)) openPaper(id);
      else { showPreview(id); $('#preview-close').focus({ preventScroll: true }); }
    });
  });
  [dock, preview].forEach(element => {
    element.addEventListener('pointerenter', () => clearTimeout(closeTimer));
    element.addEventListener('pointerleave', schedulePreviewClose);
    element.addEventListener('focusin', () => clearTimeout(closeTimer));
    element.addEventListener('focusout', schedulePreviewClose);
  });
  $('#preview-close').addEventListener('click', () => closePreview(true));
  $('#preview-open').addEventListener('click', () => { if (state.preview) openPaper(state.preview); });
  function bringForward(id) {
    Object.keys(papers).forEach(key => { papers[key].style.zIndex = key === id ? '3' : '2'; });
  }
  function renderPapers() {
    let visible = 0;
    Object.entries(papers).forEach(([id, paper]) => {
      paper.hidden = state.folded[id] || (narrow.matches && state.active !== id);
      if (!paper.hidden) visible++;
      $('[data-capsule="' + id + '"]').hidden = !state.folded[id];
      $('[data-capsule="' + id + '"]').setAttribute('aria-label', t(id === 'todo' ? 'todoTitle' : 'noteTitle'));
      $('[data-drag="' + id + '"]').disabled = narrow.matches;
      $('[data-select-paper="' + id + '"]').setAttribute('aria-pressed', String(state.active === id && !state.folded[id]));
    });
    const count = Object.values(state.folded).filter(Boolean).length;
    dock.hidden = count === 0;
    $('#master-count').textContent = count;
    $('#master-capsule').setAttribute('aria-expanded', String(state.queueOpen));
    $('#master-capsule').setAttribute('aria-label', t(state.queueOpen ? 'queueHide' : 'queueShow') + ', ' + count + ' ' + t('foldedCount'));
    $('#dock-items').hidden = !state.queueOpen;
    $('#stage-empty').hidden = visible > 0;
    stage.dataset.side = state.side;
    $('#demo-hint').textContent = t(narrow.matches ? 'hintMobile' : 'hintDesktop');
    $$('[data-scene]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.scene === state.scene)));
    if (state.preview && (!state.folded[state.preview] || !state.queueOpen)) closePreview();
    requestAnimationFrame(positionPapers);
  }
  function foldPaper(id) {
    const hadFocus = papers[id].contains(document.activeElement);
    closePreview();
    state.folded[id] = true;
    state.queueOpen = true;
    state.scene = Object.values(state.folded).every(Boolean) ? 'capsules' : null;
    renderPapers();
    if (hadFocus) focusCapsule(id);
    enter($('[data-capsule="' + id + '"]'), 4);
    announce(t('folded'));
  }
  function openPaper(id) {
    closePreview();
    state.folded[id] = false;
    state.active = id;
    state.scene = id === 'note' ? 'markdown' : 'todo';
    renderPapers();
    bringForward(id);
    const focusTarget = narrow.matches ? (id === 'todo' ? ($('[data-todo-edit]') || $('#new-todo')) : $('#mode-' + state.mode)) : $('[data-drag="' + id + '"]');
    focusTarget.focus({ preventScroll: true });
    $$('[data-todo-edit]').forEach(sizeEditor);
    enter(papers[id]);
    announce(t('opened'));
  }
  function setScene(scene) {
    closePreview();
    state.scene = scene;
    state.queueOpen = true;
    if (scene === 'capsules') {
      state.folded = { todo: true, note: true };
      announce(t('folded'));
    } else if (scene === 'markdown') {
      state.folded = { todo: true, note: false };
      state.active = 'note';
      setMode('enhanced');
      bringForward('note');
    } else {
      state.folded = { todo: false, note: false };
      state.active = 'todo';
      bringForward('todo');
    }
    renderPapers();
    $$('[data-todo-edit]').forEach(sizeEditor);
    Object.values(papers).forEach(paper => { if (!paper.hidden) enter(paper); });
  }
  $$('[data-scene]').forEach(button => button.addEventListener('click', () => setScene(button.dataset.scene)));
  $$('[data-demo-action]').forEach(link => link.addEventListener('click', () => setScene(link.dataset.demoAction)));
  $$('[data-fold]').forEach(button => button.addEventListener('click', () => foldPaper(button.dataset.fold)));
  $$('[data-select-paper]').forEach(button => button.addEventListener('click', () => openPaper(button.dataset.selectPaper)));
  $('#fold-all').addEventListener('click', () => setScene('capsules'));
  $('#master-capsule').addEventListener('click', () => {
    closePreview();
    state.queueOpen = !state.queueOpen;
    renderPapers();
  });
  $('#swap-edge').addEventListener('click', () => {
    state.side = state.side === 'left' ? 'right' : 'left';
    renderPapers();
    announce(t(state.side));
  });
  $$('[data-palette]').forEach(button => button.addEventListener('click', () => {
    playground.dataset.theme = button.dataset.palette;
    $$('[data-palette]').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
  }));
  const clamp = (value, min, max) => Math.max(min, Math.min(value, Math.max(min, max)));
  function bounds(paper) {
    return { x: Math.max(0, stage.clientWidth - paper.offsetWidth), y: Math.max(0, stage.clientHeight - paper.offsetHeight) };
  }
  function positionPapers() {
    if (narrow.matches || drag) return;
    Object.entries(papers).forEach(([id, paper]) => {
      if (paper.hidden) return;
      const max = bounds(paper);
      const saved = state.positions[id];
      const x = saved ? saved.x * max.x : id === 'todo' ? (stage.clientWidth > 650 ? 40 : 26) : max.x - 25;
      const y = saved ? saved.y * max.y : id === 'todo' ? 40 : 153;
      paper.style.left = clamp(x, 8, max.x - 8) + 'px';
      paper.style.top = clamp(y, 8, max.y - 8) + 'px';
      paper.style.right = 'auto';
    });
  }
  function storePosition(id, x, y) {
    const max = bounds(papers[id]);
    state.positions[id] = { x: max.x ? x / max.x : 0, y: max.y ? y / max.y : 0 };
  }
  function endDrag(commit = false) {
    if (!drag) return;
    const current = drag;
    drag = null;
    const paper = papers[current.id];
    const handle = $('[data-drag="' + current.id + '"]');
    paper.classList.remove('is-dragging');
    $('#drop-indicator').hidden = true;
    if (handle.hasPointerCapture(current.pointerId)) handle.releasePointerCapture(current.pointerId);
    if (commit && current.moved) {
      storePosition(current.id, current.x, current.y);
      if (current.edge) { state.side = current.edge; foldPaper(current.id); }
    }
    bringForward(current.id);
    positionPapers();
  }
  $$('[data-drag]').forEach(handle => {
    const id = handle.dataset.drag;
    papers[id].addEventListener('pointerdown', () => bringForward(id));
    papers[id].addEventListener('focusin', () => bringForward(id));
    handle.addEventListener('pointerdown', event => {
      if (narrow.matches || !event.isPrimary || event.button !== 0) return;
      event.preventDefault();
      closePreview();
      positionPapers();
      handle.focus({ preventScroll: true });
      const x = parseFloat(papers[id].style.left);
      const y = parseFloat(papers[id].style.top);
      drag = { id, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, originX: x, originY: y, x, y, moved: false, edge: null };
      handle.setPointerCapture(event.pointerId);
    });
    handle.addEventListener('pointermove', event => {
      if (!drag || drag.id !== id || drag.pointerId !== event.pointerId) return;
      const dx = event.clientX - drag.startX;
      const dy = event.clientY - drag.startY;
      if (!drag.moved && Math.hypot(dx, dy) < 4) return;
      drag.moved = true;
      const max = bounds(papers[id]);
      drag.x = clamp(drag.originX + dx, 8, max.x - 8);
      drag.y = clamp(drag.originY + dy, 8, max.y - 8);
      drag.edge = drag.x <= 14 ? 'left' : drag.x >= max.x - 14 ? 'right' : null;
      papers[id].style.left = drag.x + 'px';
      papers[id].style.top = drag.y + 'px';
      papers[id].style.zIndex = '20';
      papers[id].classList.add('is-dragging');
      $('#drop-indicator').hidden = !drag.edge;
      if (drag.edge) $('#drop-indicator').dataset.side = drag.edge;
    });
    handle.addEventListener('pointerup', event => { if (drag && event.pointerId === drag.pointerId) endDrag(true); });
    handle.addEventListener('pointercancel', () => endDrag());
    handle.addEventListener('lostpointercapture', () => endDrag());
    handle.addEventListener('keydown', event => {
      if (narrow.matches || event.ctrlKey || event.altKey || event.metaKey || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
      event.preventDefault();
      const step = event.shiftKey ? 32 : 8;
      const max = bounds(papers[id]);
      const x = clamp(parseFloat(papers[id].style.left) + (event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0), 8, max.x - 8);
      const y = clamp(parseFloat(papers[id].style.top) + (event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0), 8, max.y - 8);
      storePosition(id, x, y);
      positionPapers();
    });
  });
  stage.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || event.isComposing || event.defaultPrevented) return;
    if (drag) { endDrag(); event.preventDefault(); return; }
    if (state.preview) { closePreview(true); event.preventDefault(); return; }
    const paper = event.target.closest('.paper');
    if (paper) { foldPaper(paper.id === 'paper-todo' ? 'todo' : 'note'); event.preventDefault(); }
  });
  function resizePapers() {
    endDrag();
    closePreview();
    renderPapers();
    requestAnimationFrame(() => $$('[data-todo-edit]').forEach(sizeEditor));
  }
  narrow.addEventListener('change', resizePapers);
  new ResizeObserver(() => { if (drag) endDrag(); positionPapers(); }).observe(stage);
  $('#reset-demo').addEventListener('click', () => {
    endDrag();
    closePreview();
    state = initialState();
    undoStack = [];
    $('#new-todo').value = '';
    playground.dataset.theme = 'paper';
    $$('[data-palette]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.palette === 'paper')));
    renderTodos(); renderNote(); setMode(state.mode); renderPapers(); bringForward('note');
    announce(t('reset'));
  });
  // A deadline keeps the timer accurate after a background tab resumes. Nothing
  // ticks until the visitor starts it; hidden tabs have no scheduled repaint.
  function formatTime(milliseconds) {
    const seconds = Math.ceil(milliseconds / 1000);
    return String(Math.floor(seconds / 60)).padStart(2, '0') + ':' + String(seconds % 60).padStart(2, '0');
  }
  function renderTimer() {
    if (timerRunning) remaining = Math.max(0, deadline - Date.now());
    if (timerRunning && remaining === 0) {
      timerRunning = false;
      clearTimeout(timerHandle);
      $('#timer-status').textContent = t('timerDone');
    }
    const time = formatTime(remaining);
    $('#timer-output').textContent = time;
    $('#mini-time').textContent = time;
    $('#focus-mini').setAttribute('aria-label', t('focusTime') + ' ' + time);
    $('#timer-toggle').textContent = t(timerRunning ? 'pause' : remaining === 0 ? 'restart' : remaining < duration ? 'resume' : 'start');
    $('#focus-body').hidden = focusFolded;
    $('#focus-mini').hidden = !focusFolded;
    $('#focus-fold').textContent = t(focusFolded ? 'focusOpen' : 'focusFold');
    $('#focus-fold').setAttribute('aria-expanded', String(!focusFolded));
  }
  function tickTimer() {
    clearTimeout(timerHandle);
    renderTimer();
    if (timerRunning && !document.hidden) timerHandle = setTimeout(tickTimer, 1000);
  }
  $('#timer-toggle').addEventListener('click', () => {
    if (timerRunning) {
      remaining = Math.max(0, deadline - Date.now());
      timerRunning = false;
      $('#timer-status').textContent = t('timerPaused');
    } else {
      if (remaining === 0) remaining = duration;
      deadline = Date.now() + remaining;
      timerRunning = true;
      $('#timer-status').textContent = t(remaining === duration ? 'timerStarted' : 'timerResumed');
    }
    tickTimer();
  });
  $('#timer-reset').addEventListener('click', () => {
    timerRunning = false; remaining = duration; tickTimer();
    $('#timer-status').textContent = t('timerReset');
  });
  $('#focus-fold').addEventListener('click', () => { focusFolded = !focusFolded; renderTimer(); });
  $('#focus-mini').addEventListener('click', () => { focusFolded = false; renderTimer(); $('#timer-toggle').focus({ preventScroll: true }); });
  document.addEventListener('visibilitychange', () => {
    clearTimeout(timerHandle);
    if (!document.hidden) tickTimer();
  });
  window.addEventListener('pagehide', () => { clearTimeout(timerHandle); clearTimeout(closeTimer); });
  window.addEventListener('pageshow', tickTimer);
  function renderScriptButton() {
    $('#run-script span:last-child').textContent = t(scriptRunning ? 'scriptRunning' : scriptHasRun ? 'scriptReplay' : 'scriptRun');
    $('#run-script').disabled = scriptRunning;
  }
  $('#run-script').addEventListener('click', () => {
    clearTimeout(scriptHandle);
    scriptRunning = true;
    $('#script-idle').hidden = false;
    $('#script-result').hidden = true;
    $('#browser-address').textContent = 'about:blank';
    $('#script-status').textContent = '';
    renderScriptButton();
    scriptHandle = setTimeout(() => {
      scriptRunning = false; scriptHasRun = true;
      $('#script-idle').hidden = true;
      $('#script-result').hidden = false;
      $('#browser-address').textContent = 'https://example.com';
      $('#script-status').textContent = t('scriptDone');
      renderScriptButton();
    }, reducedMotion.matches ? 0 : 320);
  });
  function setMenu(open) {
    $('#site-menu').classList.toggle('is-open', open);
    $('#menu-toggle').setAttribute('aria-expanded', String(open));
    $('#menu-toggle').setAttribute('aria-label', t(open ? 'menuClose' : 'menuOpen'));
  }
  $('#menu-toggle').addEventListener('click', () => setMenu($('#menu-toggle').getAttribute('aria-expanded') !== 'true'));
  $('#site-menu').addEventListener('click', event => { if (event.target.closest('a')) setMenu(false); });
  document.addEventListener('pointerdown', event => { if (!event.target.closest('.site-header')) setMenu(false); });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && $('#site-menu').classList.contains('is-open')) {
      setMenu(false); $('#menu-toggle').focus();
    }
  });
  mobileNav.addEventListener('change', () => setMenu(false));
  function localize() {
    document.documentElement.lang = language === 'en' ? 'en' : 'zh-CN';
    staticText.forEach(item => {
      item.element.replaceChildren(...(language === 'en' ? [document.createTextNode(item.en)] : item.zh.map(node => node.cloneNode(true))));
    });
    staticAttributes.forEach(item => item.element.setAttribute(item.attr, language === 'en' ? item.en : item.zh));
    $('#lang-toggle').textContent = language === 'en' ? '中文' : 'EN';
    $('#lang-toggle').setAttribute('aria-label', language === 'en' ? '切换为中文' : 'Switch to English');
    const title = language === 'en' ? 'PaperTodo — A few quiet, useful papers for Windows' : initialTitle;
    const description = language === 'en' ? 'Native Windows desktop papers for todos, Markdown notes and edge capsules. Local storage, no account, free for individual use.' : initialDescription;
    document.title = title;
    $$('meta[property="og:title"], meta[name="twitter:title"]').forEach(meta => { meta.content = title; });
    $$('meta[name="description"], meta[property="og:description"], meta[name="twitter:description"]').forEach(meta => { meta.content = description; });
    $('meta[property="og:locale"]').content = language === 'en' ? 'en_US' : 'zh_CN';
    $('meta[property="og:locale:alternate"]').content = language === 'en' ? 'zh_CN' : 'en_US';
    $$('[data-readme]').forEach(link => { link.href = repo + '/blob/main/README' + (language === 'en' ? '.en' : '') + '.md'; });
    setMenu($('#site-menu').classList.contains('is-open'));
    renderTodos(); renderNote(); setMode(state.mode); renderPapers(); renderTimer(); renderScriptButton();
  }
  $('#lang-toggle').addEventListener('click', () => {
    language = language === 'en' ? 'zh' : 'en';
    try { localStorage.setItem('papertodo.website.lang', language); } catch { /* Storage is optional. */ }
    try {
      const url = new URL(location.href);
      url.searchParams.set('lang', language);
      history.replaceState(null, '', url);
    } catch { /* file:// previews can restrict history changes. */ }
    localize();
  });
  localize();
  document.documentElement.classList.add('js');
  requestAnimationFrame(() => { positionPapers(); $$('[data-todo-edit]').forEach(sizeEditor); });
})();
