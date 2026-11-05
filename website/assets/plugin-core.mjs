// Browser-only demo templates. No model calls, dynamic code execution, or desktop bridge.
export const recipes = {
  focus: {en: 'Make a 25-minute focus timer with start, pause, and reset controls.', zh: '做一个 25 分钟的专注番茄钟，可以开始、暂停和重置。'},
  habits: {en: 'Make a daily habit tracker with three small rituals and a progress bar.', zh: '做一个习惯打卡插件，记录每天的三个小习惯，带完成进度。'},
  converter: {en: 'Make a unit converter for length, temperature and weight, with a reverse direction button.', zh: '做一个单位换算插件，支持长度、温度、重量，并能切换换算方向。'}
};

export function resolveLanguage(query, stored, browserLanguage = 'en') {
  if (['en', 'zh'].includes(query)) return query;
  if (['en', 'zh'].includes(stored)) return stored;
  return /^zh(?:-|$)/i.test(browserLanguage) ? 'zh' : 'en';
}

export function planFromPrompt(value) {
  const prompt = String(value ?? '').trim();
  if (!prompt) return {ok: false, error: 'empty'};
  if (prompt.length > 500) return {ok: false, error: 'long'};
  let type;
  if (/focus|pomodoro|timer|番茄|专注|计时/i.test(prompt)) type = 'focus';
  else if (/habit|ritual|打卡|习惯/i.test(prompt)) type = 'habits';
  else if (/convert|units?|单位|换算/i.test(prompt)) type = 'converter';
  else return {ok: false, error: 'unsupported'};
  const minutes = /(-?\d+(?:\.\d+)?)\s*-?\s*(?:minutes?|mins?|分钟|分)/i.exec(prompt);
  const requestedMinutes = Number(minutes?.[1] ?? 25);
  if (type === 'focus' && (!Number.isInteger(requestedMinutes) || requestedMinutes < 1 || requestedMinutes > 120)) {
    return {ok: false, error: 'duration'};
  }
  const list = type === 'habits' ? prompt.split(/[:：]/).slice(1).join(':').trim() : '';
  const items = list ? list.split(/[,，、;；\n]/).map(x => x.trim().replace(/[。.]$/, '').slice(0, 80)).filter(Boolean).slice(0, 6) : null;
  return {ok: true, config: {type, minutes: type === 'focus' ? requestedMinutes : 25, items: items?.length ? items : null}};
}

export function escapeHTML(value) {
  return String(value).replace(/[&<>"']/g, c => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'}[c]));
}

// Exact SI definitions; the caller formats the result without changing its value.
export function convertUnits(value, kind = 'length', reversed = false) {
  if (value === null || value === undefined || String(value).trim() === '') return null;
  const number = Number(value);
  if (!Number.isFinite(number)) return null;
  let result;
  if (kind === 'temperature') result = reversed ? (number - 32) * 5 / 9 : number * 9 / 5 + 32;
  else if (kind === 'length') result = reversed ? number * 0.3048 : number / 0.3048;
  else if (kind === 'weight') result = reversed ? number * 0.45359237 : number / 0.45359237;
  else return null;
  return Number.isFinite(result) ? result : null;
}

// Self-contained so exported HTML runs the same interactions as the preview.
// Text is always assigned with textContent; prompts/items are never evaluated as code.
export function mountPlugin(root, config, initialLanguage = 'en') {
  const words = {
    en: {focus: 'Focus timer', habits: 'Daily habits', converter: 'Unit converter', tag: 'YOUR DESKTOP PLUGIN', focusHelp: 'One thing at a time. You have got this.', habitsHelp: 'A little better, one check at a time.', ready: 'ready', running: 'running', paused: 'paused', done: 'complete', start: 'Start', pause: 'Pause', resume: 'Resume', reset: 'Reset', again: 'Again', completed: 'complete', add: 'Add', newTask: 'One more thing…', taskLabel: 'New habit', limit: 'Up to 12 tasks in this demo.', rituals: ['Drink some water', 'Move for 10 minutes', 'Read a few pages'], tasks: ['Try every interaction', 'Record a short demo', 'Share it with the world']},
    zh: {focus: '专注计时', habits: '习惯打卡', converter: '单位换算', tag: '你的桌面插件', focusHelp: '一次只做一件事，慢慢来。', habitsHelp: '每一次勾选，都是一点进步。', ready: '准备就绪', running: '计时中', paused: '已暂停', done: '已完成', start: '开始', pause: '暂停', resume: '继续', reset: '重置', again: '再来一次', completed: '已完成', add: '添加', newTask: '还有一件事……', taskLabel: '新的习惯', limit: '演示最多添加 12 项任务。', rituals: ['喝一杯水', '活动十分钟', '读几页书'], tasks: ['试一遍所有交互', '录一段演示视频', '把作品分享出去']}
  };
  let lang = initialLanguage === 'zh' ? 'zh' : 'en';
  let disposed = false;
  const views = new Map();
  const el = (tag, cls, text, parent) => {const n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; if (parent) parent.append(n); return n;};

  // The paper and capsule preview are independent views of one live plugin.
  const duration = Math.min(120, Math.max(1, Number(config.minutes) || 25)) * 60_000;
  let remaining = duration, deadline = 0, running = false;
  const sampleClock = () => {
    if (running) {remaining = Math.max(0, deadline - Date.now()); if (remaining === 0) running = false;}
  };
  const units = {length:['m','ft'], temperature:['°C','°F'], weight:['kg','lb']};
  const values = {length:'1',temperature:'25',weight:'1'};
  const directions = {length:false,temperature:false,weight:false};
  const invalidValues = {length:false,temperature:false,weight:false};
  let kind = 'length';
  const isHabit = config.type === 'habits';
  const defaults = () => words[lang][isHabit ? 'rituals' : 'tasks'];
  const custom = Array.isArray(config.items) && config.items.length > 0;
  const tasks = (custom ? config.items : defaults()).slice(0, 6).map((text, i) => ({text: String(text), checked: false, translatedIndex: custom ? null : i}));
  const updateAll = () => {
    if (disposed) return;
    sampleClock();
    for (const view of views.values()) view.update();
  };

  function attachView(viewRoot) {
    if (disposed) return {dispose() {}};
    if (views.has(viewRoot)) return views.get(viewRoot).handle;
    const listeners = [];
    const on = (node, event, handler) => {node.addEventListener(event, handler); listeners.push(() => node.removeEventListener(event, handler));};
    viewRoot.replaceChildren();
    const card = el('section', 'widget', null, viewRoot);
    const title = el('h3', '', '', card);
    let update, disposeItems = () => {};
    if (config.type === 'focus') {
      const face = el('div', 'timer-face', null, card);
      const inner = el('div', 'timer-inner', null, face);
      const digits = el('div', 'timer-digits', '', inner);
      digits.setAttribute('role', 'timer');
      digits.setAttribute('aria-live', 'off');
      const state = el('div', 'timer-state', '', inner);
      state.setAttribute('role', 'status');
      const actions = el('div', 'widget-actions', null, card);
      const toggle = el('button', '', '', actions); toggle.type = 'button';
      const reset = el('button', 'secondary', '', actions); reset.type = 'button';
      update = () => {
        const w = words[lang];
        const seconds = Math.ceil(remaining / 1000);
        const time = `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
        if (digits.textContent !== time) digits.textContent = time;
        const status = running ? w.running : remaining === 0 ? w.done : remaining === duration ? w.ready : w.paused;
        if (state.textContent !== status) state.textContent = status;
        toggle.textContent = running ? w.pause : remaining === 0 ? w.again : remaining === duration ? w.start : w.resume;
        reset.textContent = w.reset;
        toggle.setAttribute('aria-pressed', String(running));
        face.style.setProperty('--progress', `${100 * (1 - remaining / duration)}%`);
      };
      on(toggle, 'click', () => {sampleClock(); if (running) running = false; else {if (!remaining) remaining = duration; deadline = Date.now() + remaining; running = true;} updateAll();});
      on(reset, 'click', () => {running = false; remaining = duration; updateAll();});
    } else if (config.type === 'converter') {
      const labels = {
        en: {length:'Length', temperature:'Temperature', weight:'Weight', from:'From', to:'To', swap:'Swap units', input:'Value to convert', empty:'Enter a number', invalid:'Enter a finite number'},
        zh: {length:'长度', temperature:'温度', weight:'重量', from:'输入', to:'换算结果', swap:'交换单位', input:'待换算数值', empty:'输入一个数值', invalid:'请输入有效数值'}
      };
      const tabs = el('div','converter-tabs',null,card); tabs.setAttribute('role','group');
      const choices = Object.keys(units).map(key => {
        const button=el('button','', '',tabs); button.type='button'; button.dataset.convertKind=key;
        on(button,'click',()=>{kind=key;updateAll();});
        return button;
      });
      const field=el('label','converter-field',null,card);
      const from=el('span','converter-label','',field);
      const entry=el('span','converter-entry',null,field);
      const input=el('input','converter-input',null,entry);
      input.type='number';input.step='any';input.inputMode='decimal';
      const sourceUnit=el('span','converter-unit','',entry);
      const result=el('div','converter-result',null,card);
      const to=el('span','converter-label','',result);
      const output=el('output','converter-output','',result);output.setAttribute('aria-live','polite');
      const actions=el('div','widget-actions',null,card);
      const swap=el('button','secondary converter-swap','',actions);swap.type='button';
      let displayedKind = '';
      update=()=>{
        const reversed=directions[kind],w=labels[lang],pair=units[kind],target=pair[reversed?0:1];
        tabs.setAttribute('aria-label',lang==='zh'?'换算类型':'Conversion type');
        choices.forEach(button=>{button.textContent=w[button.dataset.convertKind];button.setAttribute('aria-pressed',String(button.dataset.convertKind===kind));});
        from.textContent=w.from;to.textContent=w.to;sourceUnit.textContent=pair[reversed?1:0];
        // Avoid rewriting a focused number field during an incomplete keystroke.
        if(displayedKind!==kind || input.value!==values[kind] || input.validity.badInput && !invalidValues[kind])input.value=values[kind];
        displayedKind=kind;
        input.setAttribute('aria-label',`${w.input} (${sourceUnit.textContent})`);
        const number=convertUnits(values[kind],kind,reversed);
        const invalid=invalidValues[kind] || number===null && values[kind]!=='';
        input.setAttribute('aria-invalid',String(invalid));
        output.textContent=invalid?w.invalid:number===null?w.empty:`${new Intl.NumberFormat(lang==='zh'?'zh-CN':'en-US',{maximumFractionDigits:4}).format(number)} ${target}`;
        swap.textContent=`⇄ ${w.swap}`;
      };
      on(input,'input',()=>{values[kind]=input.value;invalidValues[kind]=input.validity.badInput;updateAll();});
      on(swap,'click',()=>{
        const number=convertUnits(values[kind],kind,directions[kind]);
        if(number!==null){values[kind]=String(number);invalidValues[kind]=false;}
        directions[kind]=!directions[kind];updateAll();
      });
    } else {
      const list = el('div', 'widget-list', null, card);
      const summary = el('div', 'widget-summary', null, card);
      const count = el('span', '', '', summary); count.setAttribute('role', 'status');
      const reset = el('button', '', '', summary); reset.type = 'button';
      const meter = el('div', 'widget-meter', null, card);
      const fill = el('span', '', '', meter);
      let input, add, boxes = [], lastLang = '', lastCount = -1;
      const renderItems = () => {
        disposeItems();
        const itemListeners = [];
        disposeItems = () => itemListeners.forEach(off => off());
        list.replaceChildren();
        boxes = tasks.map(task => {
          const row = el('label', 'widget-row', null, list);
          const box = el('input', '', null, row); box.type = 'checkbox'; box.checked = task.checked;
          el('span', '', task.translatedIndex === null ? task.text : defaults()[task.translatedIndex], row);
          const change = () => {task.checked = box.checked; updateAll();};
          box.addEventListener('change', change);
          itemListeners.push(() => box.removeEventListener('change', change));
          return box;
        });
        lastLang = lang;lastCount = tasks.length;
      };
      update = () => {
        if (lastLang !== lang || lastCount !== tasks.length) renderItems();
        boxes.forEach((box, i) => {box.checked = tasks[i].checked;});
        const n = tasks.filter(task => task.checked).length;
        count.textContent = `${n} / ${tasks.length} ${words[lang].completed}`;
        reset.textContent = words[lang].reset;
        fill.style.width = `${tasks.length ? n / tasks.length * 100 : 0}%`;
        if (input) {input.placeholder = tasks.length >= 12 ? words[lang].limit : words[lang].newTask; input.setAttribute('aria-label', words[lang].taskLabel); input.disabled = tasks.length >= 12; add.textContent = words[lang].add; add.disabled = tasks.length >= 12;}
      };
      on(reset, 'click', () => {tasks.forEach(task => {task.checked = false;}); updateAll();});
      if (!isHabit) {
        const form = el('form', 'widget-add', null, card);
        input = el('input', 'widget-input', null, form); input.type = 'text'; input.maxLength = 80;
        add = el('button', '', '', form); add.type = 'submit';
        on(form, 'submit', event => {event.preventDefault(); const text = input.value.trim(); if (!text || tasks.length >= 12) return; tasks.push({text, checked: false, translatedIndex: null}); input.value = ''; updateAll(); if (!input.disabled) input.focus();});
      }
    }
    const view = {
      update() {
        const label = words[lang][config.type];
        if (title.textContent !== label) {title.textContent = label; card.setAttribute('aria-label', label);}
        update();
      },
      handle: {dispose() {
        if (views.get(viewRoot) !== view) return;
        views.delete(viewRoot);
        listeners.forEach(off => off());disposeItems();
        card.remove();
      }}
    };
    views.set(viewRoot, view);
    updateAll();
    return view.handle;
  }

  attachView(root);
  const interval = config.type === 'focus' ? setInterval(() => {if (running) updateAll();}, 250) : null;
  return {
    attachView,
    setLanguage(value) {lang = value === 'zh' ? 'zh' : 'en'; updateAll();},
    dispose() {
      if (disposed) return;
      disposed = true;
      if (interval !== null) clearInterval(interval);
      for (const view of [...views.values()]) view.handle.dispose();
    }
  };
}

const exportCSS = `*{box-sizing:border-box}body{margin:0;min-height:100svh;display:grid;place-items:center;padding:24px;background:#e5eadb;color:#283523;font:15px/1.5 system-ui,sans-serif}.widget{width:min(340px,100%);padding:28px;border:1px solid #b9c5a5;border-radius:12px;background:#fbfdf0;box-shadow:0 12px 45px #33492219}.widget-tag{font-size:9px;letter-spacing:1.5px;color:#61744d}.widget h3{font:30px/1.2 Georgia,serif;margin:10px 0}.widget-help{font-size:12px;color:#61744d}.timer-face{width:200px;height:200px;border-radius:50%;padding:10px;background:conic-gradient(#8eac51 var(--progress,0%),#e1e9cf 0);margin:24px auto}.timer-inner{height:100%;display:grid;align-content:center;justify-items:center;background:#fbfdf0;border-radius:50%}.timer-digits{font-size:48px;font-variant-numeric:tabular-nums}.timer-state{font-size:11px}.widget-actions{display:flex;justify-content:center;gap:8px}button{cursor:pointer;background:#2b3925;color:#f7faef;border:0;border-radius:6px;padding:11px 18px;font:inherit;font-size:12px}.secondary{background:#e6edda;color:#304329}.widget-row{display:flex;gap:10px;align-items:center;border-bottom:1px solid #dce4d0;padding:13px 0;overflow-wrap:anywhere;font-size:14px}.widget-row input{accent-color:#506c30;flex-shrink:0}.widget-row:has(input:checked) span{text-decoration:line-through;color:#788369}.widget-summary{display:flex;justify-content:space-between;align-items:center;gap:8px;font-size:12px;margin-top:18px}.widget-summary button{background:none;color:#304329;text-decoration:underline;padding:4px}.widget-meter{height:4px;background:#dbe5ca;margin-top:10px}.widget-meter span{display:block;height:100%;background:#819e4c}.widget-add{display:flex;gap:6px;margin-top:15px}.widget-input{min-width:0;width:100%;padding:10px;border:1px solid #bacaa6;border-radius:5px;font:inherit;font-size:12px}.widget-add button{padding:9px 12px;flex-shrink:0;white-space:nowrap}button:disabled{opacity:.4;cursor:not-allowed}:focus-visible{outline:3px solid #6b8c39;outline-offset:3px}main{width:min(340px,100%)}.notice{font-size:11px;line-height:1.8;color:#536448;margin-top:20px}a{color:inherit}`;

export function exportPlugin(config, language = 'en') {
  const lang = language === 'zh' ? 'zh' : 'en';
  const json = JSON.stringify({config, language: lang}).replace(/</g, '\\u003c').replace(/>/g, '\\u003e').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');
  const notice = lang === 'zh' ? 'PaperTodo 浏览器演示 · 本地模板，无 AI 调用。刷新会重置。这不是桌面插件安装包。' : 'PaperTodo browser demo · Local template, no AI call. Refresh to reset. Not a desktop plugin package.';
  return `<!doctype html>\n<html lang="${lang === 'zh' ? 'zh-CN' : 'en'}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'none'; img-src data:; base-uri 'none'; form-action 'none'"><title>PaperTodo — ${escapeHTML(config.type)} demo</title><style>${exportCSS}</style></head><body><main><div id="plugin"></div><p class="notice">${notice}</p></main><script type="application/json" id="config">${json}</script><script>\nconst data = JSON.parse(document.getElementById('config').textContent);\n${convertUnits.toString()}\n(${mountPlugin.toString()})(document.getElementById('plugin'), data.config, data.language);\n</script></body></html>\n`;
}
