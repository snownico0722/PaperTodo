import {recipes, resolveLanguage, planFromPrompt, mountPlugin, escapeHTML} from './plugin-core.mjs';
import {CHAPTERS, MODES, createStoryState, getPaper, noteText, toggleMarkdownTask, setPluginConfig, chapterView, clamp, renderMarkdown, dockPapers, layoutEdgeQueue, createWheelPager, fitPageViewport} from './story-core.mjs';
import {translate} from './copy.mjs';
import {createSurfaceMotion} from './motion.mjs';

// One persistent workbench. Chapter changes only alter presentation; papers and the
// live plugin node are never recreated because of scrolling, language or a breakpoint.
const state = createStoryState();
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const scene = $('#desktop-scene'), canvas = $('#workbench-canvas');
const motion = matchMedia('(prefers-reduced-motion: reduce)');
const wide = matchMedia('(min-width: 980px)');
const hover = matchMedia('(hover: hover) and (pointer: fine)');
const t = key => translate(state.language, key), esc = escapeHTML;
const icons = {
  todo: '<rect x="3" y="3" width="12" height="12" rx="2"/><path d="m6 9 2 2 4-4"/>',
  note: '<path d="M4 2h7l4 4v10H4zM10 2v5h5M7 10h5M7 13h5"/>',
  link: '<path d="m7 11 4-4M6 12l-1 1a3 3 0 0 1-4-4l3-3a3 3 0 0 1 4 0m2 6a3 3 0 0 0 4 0l3-3a3 3 0 0 0-4-4l-1 1"/>',
  fold: '<path d="M4 9h10"/>', close: '<path d="m4 4 10 10M14 4 4 14"/>',
  play: '<path d="m6 3 9 6-9 6z"/>',
  tool: '<path d="m9 1 2.3 5.7L17 9l-5.7 2.3L9 17l-2.3-5.7L1 9l5.7-2.3z"/>',
  lightning: '<path d="M10 1 3 10h5l-1 7 8-10h-5z"/>'
};
const icon = name => `<svg viewBox="0 0 18 18" aria-hidden="true">${icons[name] || icons.note}</svg>`;
$$('[data-icon]').forEach(node => {node.innerHTML = icon(node.dataset.icon);});
let makerOpen = true, makerPhase = 'edit', scriptPhase = 'note', viewOverride = null;
let desktopPluginOpen = false;
let pluginHandle = null, pluginNode = null, pluginPreviewHandle = null, pluginPreviewNode = null, currentConfig = null;
let selectedRecipe = 'focus', building = false;
let buildToken = 0, buildTimer = 0, buildStatus = 'buildIdle', errorKey = '';
let markdownTimer = 0, markdownSeen = false, markdownClosed = false;
let previewDelay = 0, leaveDelay = 0, suppressedCapsule = null, toastTimer = 0;
let navigationTarget = null, navigationUntil = 0, scrollFrame = 0;
const wheelPager = createWheelPager();
let workbenchCue = null;
let pageScale = 1, pageViewport = null, viewportFrame = 0;
const surfaces = createSurfaceMotion(canvas, motion, () => pageScale);
let railSession = null, incomingToolRect = null, suppressHoverUntil = 0, todoLinkRect = null;
let previewPinned = false, pointerDownInPreview = false, previewFade = null;
let drag = null, frontOrder = 20, scriptRunTimer = 0;
let noteLayoutManual = false;
const paperPositions = new Map(), paperSizes = new Map();
const emphasisAnimations = new WeakMap();
const paperPositionKey = node => node.id === 'note-paper' && noteLayoutManual ? 'manual:note-paper' : `${liveView()}:${node.id}`;
const surfaceNodes = () => [...$$('#paper-deck > .paper, #maker, #script-editor, #script-result'), ...$$('.edge-slot')];
function rememberTodoLink() {
  const todo=$('#todo-paper'), link=$('#todo-body [data-action="linked-note"]');
  if(!todo.hidden && !todo.inert && link?.getClientRects().length) todoLinkRect=surfaces.box(link);
}
function endpoint(node, fallback) {
  const id=node.dataset.paperId;
  if(id==='note') {
    // Keep the relationship with Today even while that paper is temporarily
    // hidden for the Markdown chapter. Never fall back to the screen edge.
    const home=surfaces.box($('#paper-deck'));
    const r=todoLinkRect || {x:home.x+Math.min(240,home.width*.8),y:home.y+76,width:26,height:28};
    return {...r,opacity:0,radius:'8px'};
  }
  let anchor=id ? $(`[data-capsule="${id}"]`) : null;
  if(!anchor || !anchor.getClientRects().length || anchor.closest('[hidden]')) anchor=$('[data-capsule="todo"]');
  if(node.id==='script-editor' || node.id==='script-result') anchor=$('[data-capsule="script"]');
  if(node.id==='maker') anchor=$('[data-capsule="maker"]');
  if(node.classList.contains('edge-slot')) return {...fallback,x:canvas.clientWidth,width:0,opacity:0};
  if(anchor && anchor.getClientRects().length && !anchor.closest('[hidden]')) {
    const r=surfaces.box(anchor); return {...r,opacity:0,radius:getComputedStyle(anchor.closest('.edge-slot') || anchor).borderRadius};
  }
  return {...fallback,y:fallback.y+20,opacity:0};
}
function paperMinimum(node) {
  return node.id==='todo-paper' ? {width:200,height:138} :
    node.id==='plugin-paper' ? {width:224,height:200} : {width:220,height:164};
}
function positionPapers() {
  for(const node of $$('#paper-deck > .paper, #script-editor')) {
    node.style.left='';node.style.top='';node.style.right='';node.style.bottom='';
    node.style.width='';node.style.height='';
    if(node.hidden)continue;
    const parent=node.offsetParent, size=paperSizes.get(node.id), min=paperMinimum(node);
    if(size) {
      node.style.width=`${clamp(size.width,Math.min(min.width,parent.clientWidth),parent.clientWidth)}px`;
      node.style.height=`${clamp(size.height,Math.min(min.height,parent.clientHeight),parent.clientHeight)}px`;
    }
    const key=paperPositionKey(node), pos=paperPositions.get(key);
    if(!pos)continue;
    const x=Math.max(0,parent.clientWidth-node.offsetWidth),y=Math.max(0,parent.clientHeight-node.offsetHeight);
    // A larger canvas adds room without moving existing papers. Clamp only the
    // displayed position on small screens; keep the preferred coordinates.
    node.style.left=`${clamp(pos.x,0,x)}px`;node.style.top=`${clamp(pos.y,0,y)}px`;
    node.style.right='auto';node.style.bottom='auto';
  }
}


function liveView() {return viewOverride || chapterView(state.chapter, makerOpen);}
function title(id) {
  return id === 'todo' ? t('today') : id === 'note' ? t('noteTitle') : id==='maker' ? t('promptLabel') : id==='script' ? t('scriptTitle') : t(`${currentConfig?.type || 'focus'}Name`);
}
function announce(key) {
  clearTimeout(toastTimer); $('#toast').textContent = t(key); $('#toast').hidden = false;
  toastTimer = setTimeout(() => {$('#toast').hidden = true;}, 3600);
}
function emphasizeSurface(node, focus = true) {
  if (!node || node.hidden || node.inert || node.closest('[hidden]')) return false;
  if (node.classList.contains('paper')) node.style.zIndex=String(++frontOrder);
  const target=$('.paper-header, [data-recipe][aria-pressed="true"], [data-script="back"]',node);
  if(focus)target?.focus({preventScroll:true});
  emphasisAnimations.get(node)?.cancel();
  if (motion.matches || !node.animate) return true;
  const style=getComputedStyle(node), shadow=style.boxShadow==='none'?'0 0 0 0 transparent':style.boxShadow;
  const accent=getComputedStyle(scene).getPropertyValue('--p-active').trim();
  // Separate properties from the geometry animation: repeated activation never
  // moves/resizes a manually positioned paper or resets the maker/script state.
  const animation=node.animate([
    {boxShadow:shadow,borderColor:style.borderColor},
    {boxShadow:`0 0 0 4px color-mix(in srgb, ${accent} 38%, transparent), ${shadow}`,borderColor:accent,offset:.2},
    {boxShadow:shadow,borderColor:style.borderColor}
  ],{duration:680,easing:'ease-out'});
  animation.id='paper-emphasis';
  emphasisAnimations.set(node,animation);
  animation.finished.then(()=>{if(emphasisAnimations.get(node)===animation)emphasisAnimations.delete(node);}).catch(()=>{});
  return true;
}
function focusPaper(id) {
  emphasizeSurface($(`[data-paper-id="${id}"]`));
}
function highlightWorkbench() {
  workbenchCue?.cancel();
  const style=getComputedStyle(scene), accent=getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();
  const base={boxShadow:style.boxShadow,borderColor:style.borderColor,backgroundColor:style.backgroundColor};
  const lit={boxShadow:`0 0 0 4px color-mix(in srgb, ${accent} 30%, transparent), 0 12px 52px color-mix(in srgb, ${accent} 22%, transparent)`,borderColor:accent,backgroundColor:'#fff4d8'};
  workbenchCue=scene.animate([base,{...lit,offset:.14},{...lit,offset:.32},base],{duration:motion.matches?1:1100,easing:'ease-out'});
  workbenchCue.id='workbench-cue';
}
function readMode() {return state.chapter === 'markdown' ? state.mode : state.modeManual ? state.mode : 'full';}
function isFolded(paper) {
  const v = liveView();
  // Temporary presentation is not persisted to paper.folded.
  if (v === 'maker' || v === 'script') return true;
  if (v === 'plugins' && paper.id !== 'plugin') return true;
  if (v === 'markdown') return paper.id === 'note' ? markdownClosed : true;
  // Keep the generated tool in the queue without inserting it into a chapter's
  // initial composition. An explicit capsule click may reveal it for this visit.
  if (v === 'desktop' && paper.id === 'plugin' && !desktopPluginOpen) return true;
  return paper.folded;
}
function focusBookmark(root) {
  const a = document.activeElement;
  return root.contains(a) ? (a.dataset.key || '') : '';
}
function restoreBookmark(root, key) {
  if (key) $$('[data-key]', root).find(n => n.dataset.key === key)?.focus({preventScroll: true});
}
function todoMarkup(preview=false) {
  return getPaper(state,'todo').tasks.map(task=>{
    const check=`<input type="checkbox" data-task="${task.id}" data-key="${preview?'peek':'todo'}:${task.id}" aria-label="${esc(t('completeTask')+': '+t(task.key))}" ${task.checked?'checked':''}>`;
    const text=`<span class="task-text">${esc(t(task.key))}</span>`;
    return `<div class="todo-row">${preview?`<label class="preview-check">${check}</label>${text}`:`<label class="task-check">${check}${text}</label>`}
      ${task.linkedPaper?`<button type="button" class="task-link" data-action="linked-note" data-key="${preview?'peek-':''}linked-note" aria-label="${esc(t('linkTask'))}" title="${esc(t('linkTask'))}">${icon('link')}</button>`:''}</div>`;
  }).join('');
}
function renderTodo() {
  const key = focusBookmark($('#todo-body'));
  $('#todo-body').innerHTML = todoMarkup(); restoreBookmark($('#todo-body'), key); syncChecks();
}
function syncChecks() {
  for (const task of getPaper(state, 'todo').tasks) $$(`[data-task="${task.id}"]`).forEach(box => {box.checked = task.checked;});
  const tasks = getPaper(state, 'todo').tasks;
  $('#todo-count').textContent = `${tasks.filter(x => x.checked).length} / ${tasks.length} ${t('complete')}`;
  $('#preview-count').textContent=state.preview==='todo'?`${tasks.filter(x=>x.checked).length}/${tasks.length}`:'';
}
function renderNote() {
  const root = $('#note-body'), key = focusBookmark(root), scroll = root.scrollTop;
  root.innerHTML = `<div class="markdown" data-mode-rendered="${readMode()}">${renderMarkdown(noteText(getPaper(state, 'note'), state.language), readMode(), 'note')}</div>`;
  $('#note-mode').textContent = t(readMode()); root.scrollTop = scroll; restoreBookmark(root, key);
  $$('[data-mode]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.mode === state.mode)));
}
function stopMarkdown() {clearTimeout(markdownTimer); markdownTimer = 0;}
function startMarkdown() {
  stopMarkdown();
  if (state.chapter !== 'markdown' || state.modeManual || markdownSeen || motion.matches || document.hidden) return;
  markdownSeen = true; state.mode = 'off'; renderNote();
  let index = 0;
  const advance = () => {
    if (state.chapter !== 'markdown' || state.modeManual || document.hidden || motion.matches) return;
    state.mode = MODES[++index]; renderNote();
    if (index < MODES.length - 1) markdownTimer = setTimeout(advance, 1600);
  };
  markdownTimer = setTimeout(advance, 1600);
}
function mountLocation() {
  if (!pluginNode) return;
  // Both surfaces observe the same plugin state. Opening a capsule never takes
  // the live contents away from an already visible paper.
  const home=$('#plugin-home');
  if(pluginNode.parentElement!==home)home.append(pluginNode);
  if(state.preview==='plugin') {
    if(!pluginPreviewNode?.isConnected) {
      pluginPreviewHandle?.dispose();
      pluginPreviewNode=document.createElement('div');pluginPreviewNode.id='plugin-preview';
      $('#preview-body').append(pluginPreviewNode);
      pluginPreviewHandle=pluginHandle.attachView(pluginPreviewNode);
    }
  } else {
    pluginPreviewHandle?.dispose();pluginPreviewHandle=null;
    pluginPreviewNode?.remove();pluginPreviewNode=null;
  }
}
function renderRail() {
  const rail=$('#capsule-rail'), list=$('#capsule-items'), papers=dockPapers(state);
  rail.hidden=false; $('#queue-count').textContent=String(papers.length);
  $('#queue-toggle').setAttribute('aria-expanded',String(!state.queueClosed));
  $('#queue-glyph').textContent=state.queueClosed?'▸':'▾';
  // The wrapper and buttons retain identity while the expanded shape changes around them.
  list.hidden=false;
  for(const paper of papers) {
    let slot=$(`[data-edge-slot="${paper.id}"]`,list);
    if(!slot) {
      slot=document.createElement('div');slot.className='edge-slot';slot.dataset.edgeSlot=paper.id;
      const button=document.createElement('button');button.type='button';button.className='capsule';
      button.dataset.capsule=paper.id;button.setAttribute('aria-controls','preview-card');
      button.innerHTML=`${icon(paper.type==='plugin'||paper.type==='maker'?'tool':paper.type==='script'?'lightning':paper.type)}<span class="capsule-title"></span>`;
      slot.append(button);
      if(paper.id==='script') {
        const run=document.createElement('button');run.type='button';run.id='lightning';run.className='capsule-run';run.dataset.script='run';
        run.innerHTML='<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M5 3.5 12 8l-7 4.5z"/></svg>';slot.append(run);
      }
      list.append(slot);
    }
    const button=$('[data-capsule]',slot);
    $('.capsule-title',button).textContent=title(paper.id);
    button.title=title(paper.id);button.setAttribute('aria-label',`${t('open')}: ${title(paper.id)}`);
    button.setAttribute('aria-expanded',String(state.preview===paper.id));
    button.dataset.paperOpen=String(paper.id==='maker'?liveView()==='maker':paper.id==='script'?liveView()==='script' && scriptPhase!=='capsule':!isFolded(paper));
    slot.dataset.preview=String(state.preview===paper.id);slot.hidden=state.queueClosed;
    // The shared preview belongs to this capsule, not an unrelated floating popup.
    button.inert=state.preview===paper.id;button.tabIndex=state.preview===paper.id?-1:0;
  }
  const metrics=getComputedStyle(scene), compact=parseFloat(metrics.getPropertyValue('--capsule-hit-height')), gap=parseFloat(metrics.getPropertyValue('--capsule-gap'));
  const ids=papers.map(p=>p.id), room=Math.max(100,canvas.clientHeight-list.offsetTop-rail.offsetTop-12);
  const desired=state.preview==='plugin'?(currentConfig?.type==='focus'?252:278):state.preview==='note'?230:state.preview==='maker'?110:state.preview==='script'?160:(hover.matches?158:212);
  railSession=layoutEdgeQueue(ids,state.preview,railSession,room,desired,compact,gap);
  const previewWidth=Math.min(hover.matches?248:268,canvas.clientWidth-12);
  ids.forEach((id,i)=>{
    const slot=$(`[data-edge-slot="${id}"]`);slot.style.top=`${railSession.tops[i]}px`;
    slot.style.width=state.preview===id?`${previewWidth}px`:'var(--rail-width)';
    slot.style.height=`${state.preview===id?railSession.height:compact}px`;
  });
  if(state.preview) {
    const slot=$(`[data-edge-slot="${state.preview}"]`);
    if(slot && $('#preview-card').parentElement!==slot) slot.append($('#preview-card'));
  }
}
function renderScene() {
  rememberTodoLink();
  const before=surfaces.capture(surfaceNodes());
  if(incomingToolRect) {before.set($('#plugin-paper'),incomingToolRect);incomingToolRect=null;}
  const v = liveView();
  scene.dataset.view = v; scene.dataset.chapter = state.chapter;
  scene.dataset.palette = state.palette; scene.dataset.dark = String(state.dark);
  scene.dataset.hasPlugin = String(Boolean(currentConfig));
  $('#paper-deck').hidden=false; $('#script-surface').hidden=false; $('#maker').hidden=v!=='maker';
  for (const paper of state.papers) $(`[data-paper-id="${paper.id}"]`).hidden = isFolded(paper);
  if (!currentConfig) $('#plugin-paper').hidden = true;
  renderRail(); mountLocation(); renderScript(); renderMaker(); positionPapers(); rememberTodoLink();
  const i = CHAPTERS.indexOf(state.chapter);
  $('#previous-chapter').disabled = i === 0;
  $('#next-chapter').textContent = t(i === CHAPTERS.length - 1 ? 'finishTour' : 'nextChapter');
  $('#make-again').hidden = makerOpen;
  if (scene.dataset.ready==='true') surfaces.play(surfaceNodes(),before,endpoint);
}
// Content opacity and host geometry have separate properties, not separate copies.
// A reversing hover cancels the old completion, preserving the live tool instance.
function fadePreview(show) {
  const card=$('#preview-card'), opacity=Number(getComputedStyle(card).opacity), reversing=Boolean(previewFade);
  previewFade?.cancel();previewFade=null;
  card.inert=!show;
  const finish=()=>{if(!show && !state.preview){card.hidden=true;mountLocation();}};
  if(motion.matches || !card.animate){finish();return;}
  const animation=card.animate([{opacity:show?(reversing?opacity:0):opacity},{opacity:show?1:0}],{duration:show?180:160,easing:'ease-out',fill:'both'});
  previewFade=animation;
  animation.finished.then(()=>{if(previewFade!==animation)return;previewFade=null;finish();animation.cancel();}).catch(()=>{});
}
function hidePreview(restore = false) {
  clearTimeout(previewDelay);clearTimeout(leaveDelay);
  const previous=state.preview;if(!previous)return;
  const before=surfaces.capture($$('.edge-slot'));
  const focusInside=$('#preview-card').contains(document.activeElement);
  state.preview=null;previewPinned=false;pointerDownInPreview=false;
  // Move the single live plugin before disposing preview markup.
  fadePreview(false);
  renderRail();surfaces.play($$('.edge-slot'),before,endpoint,260);
  if(restore || focusInside) ($(`[data-capsule="${previous}"]`) || $('#queue-toggle')).focus({preventScroll:true});
}
function positionPreview() {
  if(!state.preview)return;
  if(state.queueClosed){hidePreview();return;}
  const before=surfaces.capture($$('.edge-slot'));renderRail();surfaces.play($$('.edge-slot'),before,endpoint,260);
}
function showPreview(id, keyboard = false) {
  clearTimeout(previewDelay);clearTimeout(leaveDelay);
  const paper=dockPapers(state).find(p=>p.id===id);
  if(!paper || state.queueClosed || drag)return;
  if(state.preview!==id) {
    const before=surfaces.capture($$('.edge-slot'));
    state.preview=null;mountLocation();state.preview=id;
    $('#preview-title').textContent=title(id);
    $('#preview-open').setAttribute('aria-label',`${t('open')}: ${title(id)}`);
    $('#preview-card').dataset.kind=paper.type;
    const body=$('#preview-body');body.replaceChildren();
    if(id==='todo')body.innerHTML=todoMarkup(true);
    else if(id==='maker')body.innerHTML=`<p class="capsule-summary">${esc(t('makerPeek'))}</p>`;
    else if(id==='script')body.innerHTML='<pre class="script-peek">!p<br>Write-Output "Hello, PaperTodo."</pre>';
    $('#preview-script-run').hidden=id!=='script';
    $('#preview-card').hidden=false;renderRail();mountLocation();syncChecks();renderScript();fadePreview(true);
    surfaces.play($$('.edge-slot'),before,endpoint,300);
  }
  previewPinned=keyboard;
  if(keyboard)$('#preview-open').focus({preventScroll:true});
}
function cancelFolds() {surfaces.settle();}
function foldPaper(id) {
  const paper=getPaper(state,id);if(!paper)return;
  finishDrag(false);hidePreview();paper.folded=true;state.queueClosed=false;
  if(id==='note' && state.chapter==='markdown') {markdownClosed=true;stopMarkdown();state.modeManual=true;}
  renderScene();
  // Programmatic focus must not re-trigger hover immediately after a fold.
  suppressHoverUntil=performance.now()+400;
  if(id==='note') {
    const owner=!$('#todo-paper').hidden ? $('#todo-body [data-action="linked-note"]') : state.chapter==='markdown' ? $('[data-mode][aria-pressed="true"]') : $('[data-capsule="todo"]');
    owner?.focus({preventScroll:true});return;
  }
  const target=$(`[data-capsule="${id}"]`);
  if(target && !target.closest('[hidden]'))target.focus({preventScroll:true});
  else $('[data-capsule="todo"]').focus({preventScroll:true});
}
function openPaper(id) {
  const paper=getPaper(state,id);if(!paper)return;
  finishDrag(false);hidePreview();paper.folded=false;
  if(id==='plugin')desktopPluginOpen=true;
  if(id==='note')markdownClosed=false;
  if(liveView()==='maker') {cancelBuild();makerOpen=false;}
  if(liveView()==='script' || (liveView()==='markdown' && id!=='note') || (liveView()==='plugins' && id!=='plugin')) viewOverride='desktop';
  renderScene();focusPaper(id);suppressHoverUntil=performance.now()+400;
}
function activateCapsule(id) {
  hidePreview();finishDrag(false);
  // Today always belongs to the second chapter, including when it is already
  // visible on the opening screen. Route before the repeated-click emphasis.
  if(id==='todo' && state.chapter!=='capsules')navigate('capsules');
  const current=id==='maker'?$('#maker'):id==='script'?(scriptPhase==='note'?$('#script-editor'):$('#script-result')):$(`[data-paper-id="${id}"]`);
  if(emphasizeSurface(current)) {suppressHoverUntil=performance.now()+400;return;}
  if(id==='maker') {if(state.chapter!=='studio')navigate('studio');editIdea();}
  else if(id==='script') {if(state.chapter!=='scripts')navigate('scripts');viewOverride=null;scriptPhase='note';clearTimeout(scriptRunTimer);renderScene();}
  else if(id==='note') {if(state.chapter!=='markdown')navigate('markdown');openPaper('note');}
  else openPaper(id);
}
function renderScript() {
  const running=scriptPhase==='running';
  $('#script-editor').hidden = liveView()!=='script' || scriptPhase!=='note';
  $('#script-result').hidden = liveView()!=='script' || !['running','result'].includes(scriptPhase);
  $('#script-result').dataset.running=String(running);
  $('#script-output').textContent=running?t('scriptRunning'):'Hello, PaperTodo.';
  $('#script-next').dataset.phase = scriptPhase;
  $('#script-next .cycle-label').textContent = t(['running','result'].includes(scriptPhase) ? 'scriptBack' : 'scriptRun');
  $$('[data-script="run"]').forEach(b=>{b.disabled=running;b.setAttribute('aria-label',t(running?'scriptRunning':'scriptRun'));b.title=t(running?'scriptRunning':'scriptRun');});
  const slot=$('[data-edge-slot="script"]');if(slot)slot.dataset.running=String(running);
}
function scriptAction(action) {
  const cycle=action==='next';
  if (action==='run' && scriptPhase==='running')return;
  if (state.chapter !== 'scripts')navigate('scripts');
  hidePreview();viewOverride=null;
  if (cycle) action = ['running','result'].includes(scriptPhase) ? 'back' : 'run';
  clearTimeout(scriptRunTimer);
  scriptPhase = action === 'back' ? 'note' : ['fold','close'].includes(action) ? 'capsule' : 'running';renderScene();
  suppressHoverUntil=performance.now()+500;
  if (action === 'fold' || action === 'close') $('#lightning').focus({preventScroll:true});
  else if (action === 'back') $('.paper-header', $('#script-editor')).focus({preventScroll:true});
  else {
    $('.terminal-actions [data-script="back"]').focus({preventScroll:true});
    scriptRunTimer=setTimeout(()=>{
      scriptPhase='result';renderScript();
      if(liveView()==='script' && !motion.matches)emphasizeSurface($('#script-result'),false);
    },motion.matches?180:700);
  }
  if(cycle)$('#script-next').focus({preventScroll:true});
}
function cancelBuild(reason = 'buildCancelled') {
  if (!building) return;
  ++buildToken; clearTimeout(buildTimer); building = false; buildStatus = reason;
  $('#build-progress').value = 0; renderMaker();
}
function renderMaker() {
  $('#maker').dataset.phase = makerPhase;
  $('#build-form').hidden = makerPhase === 'ready';
  $('#pipeline').hidden = !building && buildStatus === 'buildIdle' || makerPhase === 'ready';
  $('#pipeline').setAttribute('aria-busy', String(building));
  $('#build-status').textContent = t(buildStatus); $('#prompt-error').textContent = errorKey ? t(errorKey) : '';
  $('#cancel-build').hidden = !building; $('#build').disabled = building;
  $$('#build-steps li').forEach((li, i) => li.classList.toggle('is-done', i < $('#build-progress').value));
  $('#recipe-description').textContent=recipes[selectedRecipe][state.language];
  $$('[data-recipe]').forEach(b => {b.setAttribute('aria-pressed', String(b.dataset.recipe === selectedRecipe));});
  if (currentConfig) $('#tool-title').textContent = title('plugin');
}
function build() {
  if (building || liveView() !== 'maker') return;
  const plan = planFromPrompt(recipes[selectedRecipe][state.language]);
  if (!plan.ok) {errorKey = 'errorBuild'; renderMaker(); return;}
  errorKey = ''; building = true; buildStatus = 'building'; const token = ++buildToken;
  let step = 0; $('#build-progress').value = 0; renderMaker();
  const tick = () => {
    if (token !== buildToken || !building || document.hidden || liveView() !== 'maker') return;
    $('#build-progress').value = ++step;
    $$('#build-steps li').forEach((li, i) => li.classList.toggle('is-done', i < step));
    if (step < 3) {buildTimer = setTimeout(tick, motion.matches ? 100 : 460); return;}
    // Construct before committing. Failure/cancellation leaves the current slot alive.
    const candidate=document.createElement('div');candidate.id='live-plugin';let nextHandle;
    try {nextHandle=mountPlugin(candidate,plan.config,state.language);} catch(error) {
      nextHandle?.dispose();building=false;errorKey='errorBuild';buildStatus='buildCancelled';renderMaker();return;
    }
    incomingToolRect=surfaces.box($('#maker'));
    hidePreview();pluginHandle?.dispose();pluginNode?.remove();
    currentConfig={...plan.config,items:plan.config.items?[...plan.config.items]:null};
    setPluginConfig(state,currentConfig).folded=false;state.queueClosed=false;pluginNode=candidate;pluginHandle=nextHandle;
    paperSizes.delete('plugin-paper');for(const key of paperPositions.keys())if(key.endsWith(':plugin-paper'))paperPositions.delete(key);
    building=false;buildStatus='buildReady';makerPhase='ready';makerOpen=false;
    renderScene();announce('installed');
    suppressHoverUntil=performance.now()+500;
    $('[data-capsule="plugin"]').focus({preventScroll:true});

  };
  buildTimer = setTimeout(tick, motion.matches ? 100 : 380);
}
function returnToDesktop() {
  cancelBuild(); hidePreview(); makerOpen = false;
  viewOverride = currentConfig ? null : 'desktop';
  renderScene();
  // Explicit Back can return to the original papers when no plugin exists yet.
  // Build completion does not take this path or reveal unrelated papers.
  if(currentConfig)$('[data-capsule="plugin"]').focus({preventScroll:true});
  else focusPaper('todo');
}
function editIdea() {
  hidePreview(); viewOverride = null; makerOpen = true; makerPhase = 'edit'; errorKey = ''; buildStatus = 'buildIdle';
  renderScene(); $('#maker').scrollTop = 0; $(`[data-recipe="${selectedRecipe}"]`).focus({preventScroll: true});
}
function setChapter(id) {
  if (!CHAPTERS.includes(id) || state.chapter === id) return;
  finishDrag(false); stopMarkdown(); cancelBuild(); hidePreview(); state.chapter = id; markdownClosed = false; viewOverride = null; desktopPluginOpen = false;
  syncChapterUI(); renderNote(); renderScene(); startMarkdown();
}
function syncChapterUI() {
  $$('.chapter').forEach(node => {node.dataset.active = String(node.id === state.chapter);});
  $$('#navigation a, [data-chapter-link]').forEach(a => {
    if (a.hash === `#${state.chapter}`) a.setAttribute('aria-current','true'); else a.removeAttribute('aria-current');
  });
}
function navigate(id) {
  const chapter=CHAPTERS.includes(id);
  if (!chapter && id!=='features') return;
  if(chapter)setChapter(id);
  closeMenu();
  try {history.replaceState(null, '', `${location.pathname}${location.search}#${id}`);} catch {}
  const target = wide.matches || !chapter ? $(`#${id}`) : $('#story');
  navigationTarget = id; navigationUntil = performance.now() + 1000;
  syncStorySnap();
  const top = target.getBoundingClientRect().top + scrollY - storyInset();
  window.scrollTo({top, behavior: motion.matches ? 'instant' : 'smooth'});
}
function storyInset() {return (parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--nav'))+24)*pageScale;}
function syncStorySnap() {
  const inset=storyInset();
  document.documentElement.dataset.storySnap=String(wide.matches && navigationTarget!=='features' && $('#studio').getBoundingClientRect().top>=inset-2);
}
function pageWithWheel(event) {
  // Paper scrolling, horizontal gestures and browser zoom remain native.
  if (!wide.matches || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || !event.cancelable ||
      Math.abs(event.deltaX)>=Math.abs(event.deltaY) ||
      event.target.closest?.('.paper-body,.script-body,.preview-body,.maker,.terminal,textarea,select,input,[contenteditable="true"]')) return;
  const inset=storyInset();
  const inside=$('#story').getBoundingClientRect().top<=inset+2 && $('#studio').getBoundingClientRect().top>=inset-2;
  const delta=event.deltaY*(event.deltaMode===1?16:event.deltaMode===2?innerHeight:1);
  const now=performance.now(), busy=Boolean(navigationTarget && now<navigationUntil);
  const action=wheelPager.step(delta,now,inside,busy);
  if(!action.capture)return;
  event.preventDefault();
  if(!action.direction)return;
  const next=CHAPTERS.indexOf(state.chapter)+action.direction;
  if(next>=0)navigate(CHAPTERS[next] || 'features');
}
function updateScrollChapter() {
  scrollFrame = 0; syncStorySnap(); if (!wide.matches) return;
  if (navigationTarget && performance.now() < navigationUntil) return;
  navigationTarget = null;
  const threshold = innerHeight * .47;
  let active = CHAPTERS[0];
  for (const id of CHAPTERS) if ($(`#${id}`).getBoundingClientRect().top <= threshold) active = id;
  setChapter(active);
}
function closeMenu() {$('#menu-toggle').setAttribute('aria-expanded','false'); $('#navigation').classList.remove('is-open');}
function changeLanguage(value) {
  state.language = value;
  document.documentElement.lang = value === 'zh' ? 'zh-CN' : 'en'; document.title = t('pageTitle');
  $$('[data-t]').forEach(node => {node.textContent = t(node.dataset.t);});
  $$('[data-label]').forEach(node => {node.setAttribute('aria-label', t(node.dataset.label)); if (node.tagName === 'BUTTON') node.title = t(node.dataset.label);});
  $('#language').textContent = value === 'zh' ? 'EN' : '中文';
  $('#language').setAttribute('aria-label', value === 'zh' ? 'Switch to English' : '切换到中文');
  $('.guide-link').href = `https://github.com/snownico0722/PaperTodo/blob/main/doc/USER_GUIDE${value === 'en' ? '.en' : ''}.md`;
  $('#dark-toggle').setAttribute('aria-checked',String(state.dark));
  try {localStorage.setItem('papertodo-lang', value);} catch {}
  try {const url = new URL(location.href); url.searchParams.set('lang', value); history.replaceState(null,'',url);} catch {}
  renderTodo(); renderNote(); pluginHandle?.setLanguage(value);
  const preview = state.preview;
  if (preview) {hidePreview(); showPreview(preview);}
  renderScene(); syncChapterUI();
}

// Stable delegated controls. There are no creation, deletion, fake external-open
// or OS-binding buttons. Website-specific choices live in the explanation column.
document.addEventListener('click', event => {
  const b = event.target.closest('button');
  if (b?.dataset.action) {
    const {action, paper} = b.dataset;
    if (action === 'fold') foldPaper(paper);
    else if (action === 'linked-note') {getPaper(state,'todo').folded=false;openPaper('note');}
  }
  if (b?.dataset.script) scriptAction(b.dataset.script);
  if (b?.dataset.mode) {stopMarkdown(); state.modeManual = true; state.mode = b.dataset.mode; markdownClosed = false; viewOverride = null; renderNote(); renderScene();}
  if (b?.dataset.paletteChoice) {state.palette = b.dataset.paletteChoice; $$('[data-palette-choice]').forEach(n => n.setAttribute('aria-pressed',String(n===b))); renderScene();}
  if (b?.dataset.recipe) {
    cancelBuild(); selectedRecipe = b.dataset.recipe; errorKey = ''; renderMaker();
  }
  if (b?.dataset.capsule) {
    clearTimeout(previewDelay); suppressedCapsule = null;
    activateCapsule(b.dataset.capsule);
  }
  const anchor = event.target.closest('a[href^="#"]');
  if (anchor && [...CHAPTERS,'features'].includes(anchor.hash.slice(1))) {event.preventDefault(); navigate(anchor.hash.slice(1));}
  else if (anchor) closeMenu();
});
document.addEventListener('change', event => {
  const n = event.target;
  if (n.dataset.task) {const task = getPaper(state,'todo').tasks.find(x => x.id === n.dataset.task); if (task) {task.checked = n.checked; syncChecks();}}
  if (n.dataset.mdCheck !== undefined && n.dataset.note === 'note') {
    state.modeManual = true; stopMarkdown(); toggleMarkdownTask(getPaper(state,'note'),state.language,Number(n.dataset.mdCheck),n.checked);
    $$(`[data-md-check="${n.dataset.mdCheck}"][data-note="note"]`).forEach(box=>{box.checked=n.checked;});
  }
});
$('#note-body').addEventListener('focusin', () => {stopMarkdown();});
$('#build-form').addEventListener('submit', event => {event.preventDefault(); build();});
$('#cancel-build').addEventListener('click', () => {cancelBuild(); $('#build').focus({preventScroll:true});});
$('#maker-back').addEventListener('click', returnToDesktop);
$('#make-again').addEventListener('click', editIdea);
$('#language').addEventListener('click', () => changeLanguage(state.language === 'zh' ? 'en' : 'zh'));
$('#dark-toggle').addEventListener('click', () => {state.dark = !state.dark; $('#dark-toggle').setAttribute('aria-checked',String(state.dark)); renderScene();});
$('#try-demo').addEventListener('click', () => {
  setChapter('overview');getPaper(state,'todo').folded = false; renderScene();
  const first = $('#todo-body input'); first.focus({preventScroll: true});
  highlightWorkbench();
  if (!wide.matches) scene.scrollIntoView({behavior:motion.matches ? 'instant' : 'smooth',block:'start'});
});
$('#queue-toggle').addEventListener('click', () => {hidePreview(); state.queueClosed = !state.queueClosed; renderScene();});
$('#preview-close').addEventListener('click', () => {suppressedCapsule = state.preview; hidePreview(true);});
$('#preview-open').addEventListener('click', () => activateCapsule(state.preview));
// Physical pointer position is authoritative. The narrow vertical gaps between
// visible queue members are a transfer corridor, not a giant invisible hover box.
let lastPointer={x:-1,y:-1};
function pointIn(r,x,y){return x>=r.left && x<=r.right && y>=r.top && y<=r.bottom;}
function pointerRegion(x,y) {
  const slots=$$('.edge-slot:not([hidden])');
  for(const slot of slots) if(pointIn(slot.getBoundingClientRect(),x,y))return slot.dataset.edgeSlot;
  const rects=slots.map(n=>n.getBoundingClientRect()).sort((a,b)=>a.top-b.top);
  for(let i=0;i<rects.length-1;i++)if(y>=rects[i].bottom && y<=rects[i+1].top && x>=Math.max(rects[i].left,rects[i+1].left) && x<=Math.min(rects[i].right,rects[i+1].right))return 'corridor';
  return null;
}
function inspectPointer(event) {
  if(!hover.matches || event.pointerType==='touch' || drag || performance.now()<suppressHoverUntil)return;
  lastPointer={x:event.clientX,y:event.clientY};
  const id=pointerRegion(event.clientX,event.clientY);
  if(event.target.closest('.capsule-run')){clearTimeout(previewDelay);clearTimeout(leaveDelay);return;}
  if(suppressedCapsule && id===suppressedCapsule)return;
  if(id && id!=='corridor') {
    clearTimeout(leaveDelay);
    if(id===state.preview)return;
    clearTimeout(previewDelay);
    previewDelay=setTimeout(()=>{
      if(pointerRegion(lastPointer.x,lastPointer.y)===id && !pointerDownInPreview)showPreview(id);
    },state.preview?110:70);
  } else if(!id) {
    suppressedCapsule=null;clearTimeout(previewDelay);clearTimeout(leaveDelay);
    if(state.preview && !previewPinned && !pointerDownInPreview)leaveDelay=setTimeout(()=>{
      if(!pointerRegion(lastPointer.x,lastPointer.y) && !pointerDownInPreview)hidePreview();
    },130);
  } else {clearTimeout(leaveDelay);clearTimeout(previewDelay);}
}
canvas.addEventListener('pointermove',inspectPointer);
canvas.addEventListener('pointerleave',event=>{
  lastPointer={x:-1,y:-1};clearTimeout(previewDelay);
  if(event.pointerType!=='touch' && !previewPinned && !pointerDownInPreview){clearTimeout(leaveDelay);leaveDelay=setTimeout(()=>hidePreview(),130);}
});
$('#preview-card').addEventListener('pointerdown',()=>{pointerDownInPreview=true;clearTimeout(leaveDelay);});
window.addEventListener('pointerup',()=>{
  pointerDownInPreview=false;
  if(state.preview && hover.matches && !previewPinned && !pointerRegion(lastPointer.x,lastPointer.y))leaveDelay=setTimeout(()=>hidePreview(),130);
});
$('#preview-card').addEventListener('click',event=>{
  // Native preview checkboxes/links consume input; remaining content opens the paper.
  if(!event.target.closest('button,input,label,a,textarea,select,.widget'))activateCapsule(state.preview);
});
$('#capsule-items').addEventListener('keydown',event=>{
  const b=event.target.closest('[data-capsule]');if(!b)return;
  if(event.key==='ArrowLeft'){event.preventDefault();showPreview(b.dataset.capsule,true);}
  else if(event.key==='ArrowDown' || event.key==='ArrowUp'){
    event.preventDefault();const buttons=$$('[data-capsule]', $('#capsule-items')),i=buttons.indexOf(b);
    buttons[(i+(event.key==='ArrowDown'?1:-1)+buttons.length)%buttons.length].focus({preventScroll:true});
  }
});
// Tap activates a destination. Long press offers the hover preview on touch screens.
let touchPreviewTimer=0,touchOrigin=null,suppressTouchClickUntil=0;
canvas.addEventListener('pointerdown',event=>{
  const button=event.target.closest('[data-capsule]');
  if(event.pointerType!=='touch' || !button)return;
  touchOrigin={x:event.clientX,y:event.clientY};clearTimeout(touchPreviewTimer);
  touchPreviewTimer=setTimeout(()=>{showPreview(button.dataset.capsule,true);suppressTouchClickUntil=performance.now()+800;touchOrigin=null;},420);
});
canvas.addEventListener('pointermove',event=>{
  if(touchOrigin && Math.hypot(event.clientX-touchOrigin.x,event.clientY-touchOrigin.y)>8){clearTimeout(touchPreviewTimer);touchOrigin=null;}
});
for(const name of ['pointerup','pointercancel','pointerleave'])canvas.addEventListener(name,()=>{clearTimeout(touchPreviewTimer);touchOrigin=null;});
canvas.addEventListener('click',event=>{if(performance.now()<suppressTouchClickUntil){suppressTouchClickUntil=0;event.preventDefault();event.stopImmediatePropagation();}},true);
canvas.addEventListener('contextmenu',event=>{const b=event.target.closest('[data-capsule]');if(b){event.preventDefault();showPreview(b.dataset.capsule,true);}});
// One geometry owner for moving and resizing. Preferred sizes survive folding,
// chapter switches, translations and a temporarily smaller viewport.
function localPaperRect(node) {
  const r=node.getBoundingClientRect(),parent=node.offsetParent,p=parent.getBoundingClientRect();
  return {x:(r.left-p.left)/pageScale-parent.clientLeft,y:(r.top-p.top)/pageScale-parent.clientTop,width:r.width/pageScale,height:r.height/pageScale,parent};
}
function rememberPosition(node) {
  const r=localPaperRect(node);
  paperPositions.set(paperPositionKey(node),{x:r.x,y:r.y});
}
function claimNoteLayout(node) {
  if(node.id!=='note-paper' || !node.offsetParent)return;
  const r=localPaperRect(node);
  noteLayoutManual=true;node.dataset.layout='manual';
  paperSizes.set(node.id,{width:r.width,height:r.height});rememberPosition(node);
}
function movePaper(node,dx,dy) {
  if(!node.offsetParent)return;const r=localPaperRect(node);
  const x=clamp(r.x+dx,0,Math.max(0,r.parent.clientWidth-r.width)),y=clamp(r.y+dy,0,Math.max(0,r.parent.clientHeight-r.height));
  node.style.left=`${x}px`;node.style.top=`${y}px`;node.style.right='auto';node.style.bottom='auto';rememberPosition(node);
}
function resizePaper(node,dx,dy) {
  if(!node.offsetParent)return;const r=localPaperRect(node),min=paperMinimum(node);
  const maxW=r.parent.clientWidth-r.x,maxH=r.parent.clientHeight-r.y;
  // getBoundingClientRect retains subpixels. offsetWidth rounding on each move
  // accumulates drift over a long drag, making the edge outrun the pointer.
  const width=clamp(r.width+dx,Math.min(min.width,maxW),maxW),height=clamp(r.height+dy,Math.min(min.height,maxH),maxH);
  node.style.left=`${r.x}px`;node.style.top=`${r.y}px`;node.style.right='auto';node.style.bottom='auto';
  node.style.width=`${width}px`;node.style.height=`${height}px`;
  paperSizes.set(node.id,{width,height});rememberPosition(node);
}
function finishDrag(cancel=false) {
  if(!drag)return;const d=drag;drag=null;
  d.node.classList.remove('is-dragging','is-resizing');
  if(cancel){
    d.previous ? paperPositions.set(d.key,d.previous) : paperPositions.delete(d.key);
    d.previousSize ? paperSizes.set(d.node.id,d.previousSize) : paperSizes.delete(d.node.id);positionPapers();
  } else if(d.moved)claimNoteLayout(d.node);
  if(d.header.hasPointerCapture(d.pointer))d.header.releasePointerCapture(d.pointer);
}
canvas.addEventListener('pointerdown',event=>{
  const resize=event.target.closest('[data-resize]'),header=resize || event.target.closest('[data-drag-handle]');
  if(!header || event.button!==0 || (!resize && event.target.closest('button,input,a,textarea,select')) || header.closest('[inert]'))return;
  const node=header.closest('.paper');if(!node || node.hidden)return;
  surfaces.settle();hidePreview();surfaces.settle();
  if(header.tabIndex>=0)header.focus({preventScroll:true});node.style.zIndex=String(++frontOrder);
  drag={node,header,resize:resize?.dataset.resize,pointer:event.pointerId,x:event.clientX,y:event.clientY,startX:event.clientX,startY:event.clientY,moved:false,key:paperPositionKey(node),previous:paperPositions.get(paperPositionKey(node)),previousSize:paperSizes.get(node.id)};
  header.setPointerCapture(event.pointerId);event.preventDefault();
});
canvas.addEventListener('pointermove',event=>{
  if(!drag || event.pointerId!==drag.pointer)return;
  if(!drag.moved && Math.hypot(event.clientX-drag.startX,event.clientY-drag.startY)<3)return;
  drag.moved=true;drag.node.classList.add(drag.resize?'is-resizing':'is-dragging');
  const dx=(event.clientX-drag.x)/pageScale,dy=(event.clientY-drag.y)/pageScale;
  if(drag.resize)resizePaper(drag.node,drag.resize.includes('e')?dx:0,drag.resize.includes('s')?dy:0);
  else movePaper(drag.node,dx,dy);
  drag.x=event.clientX;drag.y=event.clientY;
});
canvas.addEventListener('pointerup',()=>finishDrag(false));
canvas.addEventListener('pointercancel',()=>{pointerDownInPreview=false;finishDrag(true);});
canvas.addEventListener('lostpointercapture',()=>finishDrag(false));
canvas.addEventListener('keydown',event=>{
  if(!event.target.matches('[data-drag-handle],[data-resize]') || event.altKey || event.ctrlKey || event.metaKey)return;
  const deltas={ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]};
  if(!deltas[event.key])return;event.preventDefault();surfaces.settle();
  const amount=event.shiftKey?28:8,[x,y]=deltas[event.key];
  (event.target.dataset.resize?resizePaper:movePaper)(event.target.closest('.paper'),x*amount,y*amount);
  claimNoteLayout(event.target.closest('.paper'));
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {if(drag){event.preventDefault();finishDrag(true);return;}closeMenu(); if (state.preview) {event.preventDefault(); hidePreview(true);} else if (building) {event.preventDefault(); cancelBuild(); $('#build').focus({preventScroll:true});} return;}
  if (event.altKey || event.ctrlKey || event.metaKey || event.target.closest?.('input,textarea,select,[contenteditable],pre,.paper-body,.preview-body,#maker')) return;
  const direction = event.key === 'PageDown' ? 1 : event.key === 'PageUp' ? -1 : 0;
  if (direction && $('#story').getBoundingClientRect().bottom > innerHeight / 2 && $('#story').getBoundingClientRect().top < innerHeight / 2) {
    const i = CHAPTERS.indexOf(state.chapter), next = i + direction;
    if (next >= 0 && next < CHAPTERS.length) {event.preventDefault(); navigate(CHAPTERS[next]);}
  }
});
$('#menu-toggle').addEventListener('click', () => {const open = $('#menu-toggle').getAttribute('aria-expanded') !== 'true'; $('#menu-toggle').setAttribute('aria-expanded',String(open)); $('#navigation').classList.toggle('is-open',open);});
$('#previous-chapter').addEventListener('click', () => navigate(CHAPTERS[Math.max(0,CHAPTERS.indexOf(state.chapter)-1)]));
$('#next-chapter').addEventListener('click', () => {const i = CHAPTERS.indexOf(state.chapter);navigate(CHAPTERS[i+1] || 'features');});
window.addEventListener('wheel',pageWithWheel,{passive:false});
function syncPageViewport(preserveReading = false) {
  const root=document.documentElement;
  const next=fitPageViewport(innerWidth,innerHeight,innerWidth-root.clientWidth);
  if(pageViewport && Object.keys(next).every(key=>Math.abs(next[key]-pageViewport[key])<.001))return;
  // Preserve the current reading point, including free scrolling below the tour.
  const anchors=$$('.chapter, #features, #faq, #download, footer').filter(node=>node.getClientRects().length);
  const reading=preserveReading && pageViewport?.width>=980 && wide.matches ?
    anchors.filter(node=>node.getBoundingClientRect().top<=storyInset()+1).at(-1) : null;
  const offset=reading ? (reading.getBoundingClientRect().top-storyInset())/pageScale : 0;
  finishDrag(false);surfaces.settle();wheelPager.reset();
  pageViewport=next;pageScale=next.scale;
  root.style.setProperty('--page-scale',String(pageScale));
  root.style.setProperty('--layout-width',`${next.contentWidth}px`);
  root.style.setProperty('--layout-vw',`${next.width/100}px`);
  root.style.setProperty('--layout-vh',`${next.height/100}px`);
  positionPapers();rememberTodoLink();if(state.preview)positionPreview();
  if(reading)window.scrollTo({top:reading.getBoundingClientRect().top+scrollY-storyInset()-offset*pageScale,behavior:'instant'});
  syncStorySnap();
}
window.addEventListener('resize', () => {
  if(!viewportFrame)viewportFrame=requestAnimationFrame(()=>{viewportFrame=0;syncPageViewport(true);});
});
window.addEventListener('scroll', () => {if (!scrollFrame) scrollFrame=requestAnimationFrame(updateScrollChapter);},{passive:true});
window.addEventListener('scrollend', () => {navigationTarget=null;updateScrollChapter();});
window.addEventListener('hashchange', () => {const id = location.hash.slice(1); if (CHAPTERS.includes(id)) navigate(id);});
wide.addEventListener('change', () => {wheelPager.reset();finishDrag(false);cancelFolds();hidePreview();syncChapterUI();positionPapers();renderScene();syncStorySnap();});
motion.addEventListener('change', () => {stopMarkdown(); cancelFolds(); renderScene();});
document.addEventListener('visibilitychange', () => {if (document.hidden) {finishDrag(false);stopMarkdown();cancelBuild();hidePreview();surfaces.settle();clearTimeout(previewDelay);} });
window.addEventListener('pagehide', event => {if (!event.persisted) {stopMarkdown(); cancelBuild();clearTimeout(scriptRunTimer); pluginHandle?.dispose();}});
new ResizeObserver(() => {finishDrag(false);surfaces.settle();positionPapers();if(state.preview)positionPreview();}).observe(canvas);
let stored = null;
try {stored=localStorage.getItem('papertodo-lang');} catch {}
state.language = resolveLanguage(document.documentElement.dataset.language || new URLSearchParams(location.search).get('lang'), stored, navigator.language);
const first = location.hash.slice(1); if (CHAPTERS.includes(first)) state.chapter = first;
syncPageViewport();
changeLanguage(state.language); syncChapterUI(); renderScene();
syncStorySnap();
scene.dataset.ready = 'true';
if (state.chapter === 'markdown') startMarkdown();
if (first && CHAPTERS.includes(first) && wide.matches) requestAnimationFrame(() => navigate(first));
