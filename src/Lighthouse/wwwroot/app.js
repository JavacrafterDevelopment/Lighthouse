/* ============================================================================
   Lighthouse front-end.

   The results list is fully virtualised over the whole match set, which can be
   millions of rows: the scroller is sized to total * ROW_H, and only the rows in
   view exist in the DOM. Pages of results are pulled from the host on demand and
   cached, so scrolling never re-queries what it already has.
   ========================================================================== */

const ROW_H     = 54;
const PAGE      = 200;
const OVERSCAN  = 8;

const host = window.chrome.webview;

const el = {
  q:          document.getElementById('q'),
  clear:      document.getElementById('clear'),
  scroller:   document.getElementById('scroller'),
  sizer:      document.getElementById('sizer'),
  rows:       document.getElementById('rows'),
  empty:      document.getElementById('empty'),
  emptyTitle: document.getElementById('emptyTitle'),
  emptyText:  document.getElementById('emptyText'),
  statusText: document.getElementById('statusText'),
  counts:     document.getElementById('counts'),
  lamp:       document.getElementById('lamp'),
  menu:       document.getElementById('menu'),
  reindex:    document.getElementById('reindex'),
  gear:       document.getElementById('gear'),
  setwrap:    document.getElementById('setwrap'),
  setClose:   document.getElementById('set-close'),
  setTray:    document.getElementById('set-tray'),
  setDark:    document.getElementById('set-dark'),
};

const state = {
  query:    '',
  mode:     'smart',
  types:    'all',
  tidy:     false,      // hide build output, package caches and system machinery
  seq:      0,          // query generation; bumped whenever the result set changes
  reqId:    0,
  total:    0,
  pages:    new Map(),  // pageIndex -> rows[]
  pending:  new Set(),
  selected: -1,
  indexed:  0,
  phase:    'starting',
  lastUs:   0,
  nodes:    new Map(),  // rowIndex -> element
  lastRefresh: 0,       // throttles re-fetching while a scan is running
};

/* ------------------------------------------------------------------ query */

/* A new query starts from the top with nothing selected. */
function runQuery() {
  state.selected = -1;
  state.total = 0;
  el.scroller.scrollTop = 0;
  invalidateResults();
}

/* The index keeps growing while a scan runs, so results need re-fetching — but
   yanking the list back to the top and dropping the selection mid-scroll would be
   hostile. This refreshes in place instead. */
function refreshResults() {
  const top = el.scroller.scrollTop;
  invalidateResults();
  el.scroller.scrollTop = top;
}

function invalidateResults() {
  state.seq++;
  state.pages.clear();
  state.pending.clear();
  state.nodes.clear();
  el.rows.replaceChildren();
  fetchPage(Math.floor(el.scroller.scrollTop / ROW_H / PAGE));
  fetchPage(0);
}

function fetchPage(page) {
  if (state.pages.has(page) || state.pending.has(page)) return;
  state.pending.add(page);
  host.postMessage({
    cmd:    'search',
    id:     ++state.reqId,
    seq:    state.seq,
    query:  state.query,
    offset: page * PAGE,
    limit:  PAGE,
    mode:   state.mode,
    types:  state.types,
    tidy:   state.tidy,
    hidden: true,
    system: false,
  });
}

host.addEventListener('message', (e) => {
  const m = e.data;
  if (m.evt === 'status')    return onStatus(m);
  if (m.evt === 'focus')     return focusSearch();
  if (m.evt === 'setQuery')  return setQuery(m.query, m.types, m.tidy);
  if (m.evt === 'shellMenu')    return onShellMenu(m);
  if (m.evt === 'openMenu')     return openMenuWhenReady();
  if (m.evt === 'settings')     return applySettings(m);
  if (m.evt === 'openSettings') return openSettings();
  if (typeof m.seq === 'number') return onResults(m);
});

function onResults(m) {
  if (m.seq !== state.seq) return;   // belongs to an older query

  if (m.error) {
    el.statusText.textContent = 'Search failed — ' + m.error;
    el.lamp.className = '';
    return;
  }

  const page = Math.floor(m.offset / PAGE);
  state.pending.delete(page);
  state.pages.set(page, m.rows);
  state.total = m.total;
  state.indexed = m.indexed;
  state.lastUs = m.us;

  el.sizer.style.height = (state.total * ROW_H) + 'px';
  render();
  updateStatus();
  updateEmpty();
}

/* -------------------------------------------------------------- rendering */

function render() {
  const top    = el.scroller.scrollTop;
  const height = el.scroller.clientHeight;

  let first = Math.max(0, Math.floor(top / ROW_H) - OVERSCAN);
  let last  = Math.min(state.total - 1, Math.ceil((top + height) / ROW_H) + OVERSCAN);

  for (let p = Math.floor(first / PAGE); p <= Math.floor(Math.max(first, last) / PAGE); p++) {
    fetchPage(p);
  }

  // Retire rows that scrolled out of the window.
  for (const [i, node] of state.nodes) {
    if (i < first || i > last) { node.remove(); state.nodes.delete(i); }
  }

  const frag = document.createDocumentFragment();
  for (let i = first; i <= last; i++) {
    const row = rowAt(i);
    if (!row) continue;

    let node = state.nodes.get(i);
    if (!node) {
      node = buildRow(row, i);
      state.nodes.set(i, node);
      frag.appendChild(node);
    }
    node.style.top = (i * ROW_H) + 'px';
    node.classList.toggle('sel', i === state.selected);
  }
  if (frag.childNodes.length) el.rows.appendChild(frag);
}

function rowAt(i) {
  const page = state.pages.get(Math.floor(i / PAGE));
  return page ? page[i % PAGE] : null;
}

function buildRow(r, i) {
  const node = document.createElement('div');
  node.className = 'row' + (r.dir ? ' folder' : '');
  node.dataset.i = i;

  const icon = document.createElement('div');
  icon.className = 'ico';
  const img = document.createElement('img');
  img.draggable = false;
  img.alt = '';
  // Leave a clean gap rather than a broken-image glyph if a lookup ever fails.
  img.onerror = () => { img.style.visibility = 'hidden'; };
  img.src = iconUrl(r);
  icon.appendChild(img);

  const cell = document.createElement('div');
  cell.className = 'namecell';

  const name = document.createElement('div');
  name.className = 'name';
  name.innerHTML = nameHtml(r);

  const path = document.createElement('div');
  path.className = 'path';
  path.textContent = r.d;

  cell.append(name, path);

  const type = document.createElement('div');
  type.className = 'type';
  type.textContent = r.t;

  const size = document.createElement('div');
  size.className = 'size';
  size.textContent = r.dir ? '—' : formatSize(r.s);

  const date = document.createElement('div');
  date.className = 'date';
  date.textContent = formatDate(r.m);

  node.append(icon, cell, type, size, date);
  return node;
}

/* Icons come from their own origin: requests to the folder-mapped host are served
   inside WebView2 and never reach the C# handler. */
function iconUrl(r) {
  return 'https://lighthouse-icons.local/icon?k=' + encodeURIComponent(r.k)
       + '&p=' + encodeURIComponent(r.p)
       + '&d=' + (r.dir ? '1' : '0')
       + '&s=48';
}

/* Renders the file name with the matched span marked, keeping the extension
   visually distinct so it is always readable at a glance. */
function nameHtml(r) {
  const name = r.n;
  const extAt = r.e ? name.length - r.e.length : name.length;
  const base = name.slice(0, extAt);
  const ext  = name.slice(extAt);

  const needle = lastSegment(state.query);
  let start = -1;
  if (needle) start = name.toLowerCase().indexOf(needle.toLowerCase());
  const end = start >= 0 ? start + needle.length : -1;

  let html = markRange(base, start, end, 0);
  if (ext) html += '<span class="ext">' + markRange(ext, start, end, extAt) + '</span>';
  return html;
}

function markRange(text, start, end, shift) {
  if (start < 0) return escapeHtml(text);
  const s = Math.max(0, start - shift);
  const e = Math.min(text.length, end - shift);
  if (e <= 0 || s >= text.length || s >= e) return escapeHtml(text);
  return escapeHtml(text.slice(0, s))
       + '<mark>' + escapeHtml(text.slice(s, e)) + '</mark>'
       + escapeHtml(text.slice(e));
}

function lastSegment(q) {
  const i = Math.max(q.lastIndexOf('\\'), q.lastIndexOf('/'));
  return i >= 0 ? q.slice(i + 1) : q;
}

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

function formatSize(bytes) {
  if (bytes < 0) return '—';
  if (bytes < 1024) return bytes + ' B';
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = bytes / 1024, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return (v < 10 ? v.toFixed(1) : Math.round(v)) + ' ' + units[u];
}

function formatDate(ms) {
  if (!ms) return '—';
  const d = new Date(ms);
  const now = new Date();
  const sameYear = d.getFullYear() === now.getFullYear();
  return d.toLocaleDateString(undefined, sameYear
    ? { day: '2-digit', month: 'short' }
    : { day: '2-digit', month: 'short', year: 'numeric' })
    + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

/* ------------------------------------------------------------ status area */

function onStatus(m) {
  state.phase = m.phase;
  state.indexed = m.count;
  el.statusText.textContent = m.message;
  const scanning = m.phase === 'scanning' || m.phase === 'starting';
  el.lamp.className = scanning ? 'scanning'
                    : m.phase === 'limited' ? 'limited'
                    : m.phase === 'failed'  ? '' : 'ready';

  // Offered only once a scan has settled, so it cannot be fired mid-scan.
  el.reindex.classList.toggle('on', !scanning);

  // The index grows while scanning, so keep what is on screen current — without
  // disturbing the selection, the scroll position, or an open context menu.
  const now = Date.now();
  if (now - state.lastRefresh > 600) {
    state.lastRefresh = now;
    refreshResults();
  }
  updateEmpty();
}

function updateStatus() {
  const parts = [];
  if (state.query) parts.push('<b>' + state.total.toLocaleString() + '</b> ' + (state.total === 1 ? 'match' : 'matches'));
  else parts.push('<b>' + state.total.toLocaleString() + '</b> items');
  parts.push('<b>' + state.indexed.toLocaleString() + '</b> indexed');
  if (state.lastUs >= 0) {
    const ms = state.lastUs / 1000;
    parts.push('<b>' + (ms < 1 ? ms.toFixed(2) : ms.toFixed(1)) + '</b> ms');
  }
  el.counts.innerHTML = parts.join(' <span class="sep">·</span> ');
}

function updateEmpty() {
  const scanning = state.phase === 'scanning' || state.phase === 'starting';
  const show = state.total === 0;
  el.empty.classList.toggle('on', show);
  if (!show) return;

  if (scanning) {
    el.emptyTitle.textContent = 'Building the index';
    el.emptyText.innerHTML = 'Reading the file table straight off your drives. This usually takes a few seconds.';
  } else if (state.query) {
    el.emptyTitle.textContent = 'Nothing matches “' + escapeHtml(state.query) + '”';
    el.emptyText.innerHTML = 'Try fewer letters, or turn off the <kbd>Starts with</kbd> filter to match anywhere in the name.';
  } else {
    el.emptyTitle.textContent = 'Nothing indexed';
    el.emptyText.innerHTML = 'No drives could be read. Restart Lighthouse and accept the administrator prompt.';
  }
}

/* -------------------------------------------------------------- selection */

function select(i, scroll) {
  if (state.total === 0) return;
  const next = Math.max(0, Math.min(state.total - 1, i));
  const prev = state.nodes.get(state.selected);
  if (prev) prev.classList.remove('sel');
  state.selected = next;

  if (scroll) {
    const top = next * ROW_H;
    const view = el.scroller.scrollTop;
    const h = el.scroller.clientHeight;
    if (top < view) el.scroller.scrollTop = top;
    else if (top + ROW_H > view + h) el.scroller.scrollTop = top + ROW_H - h;
  }
  render();
}

function selectedRow() {
  return state.selected >= 0 ? rowAt(state.selected) : null;
}

function act(action, row) {
  if (!row) return;
  switch (action) {
    case 'open':       host.postMessage({ cmd: 'open', path: row.p }); break;
    case 'openFolder': host.postMessage({ cmd: 'openFolder', path: row.p }); break;
    case 'reveal':     host.postMessage({ cmd: 'reveal', path: row.p }); break;
    case 'copyPath':   host.postMessage({ cmd: 'copy', text: row.p }); break;
    case 'copyName':   host.postMessage({ cmd: 'copy', text: row.n }); break;
  }
}

/* ----------------------------------------------------------------- events */

el.q.addEventListener('input', () => {
  state.query = el.q.value;
  el.clear.classList.toggle('on', state.query.length > 0);
  runQuery();
});

el.clear.addEventListener('click', () => {
  el.q.value = '';
  state.query = '';
  el.clear.classList.remove('on');
  runQuery();
  el.q.focus();
});

function startReindex() {
  if (!el.reindex.classList.contains('on')) return;  // a scan is already running
  el.reindex.classList.remove('on');
  el.statusText.textContent = 'Re-indexing';
  el.lamp.className = 'scanning';
  host.postMessage({ cmd: 'reindex' });
}

el.reindex.addEventListener('click', startReindex);

el.scroller.addEventListener('scroll', () => {
  requestAnimationFrame(render);
}, { passive: true });

window.addEventListener('resize', () => requestAnimationFrame(render));

el.rows.addEventListener('mousedown', (e) => {
  const row = e.target.closest('.row');
  if (row) select(+row.dataset.i, false);
});

el.rows.addEventListener('dblclick', (e) => {
  const row = e.target.closest('.row');
  if (row) act('open', rowAt(+row.dataset.i));
});

el.rows.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  const row = e.target.closest('.row');
  if (!row) return;
  select(+row.dataset.i, false);
  // Shift+right-click asks Windows for the extended verb set, same as Explorer.
  requestMenu(e.clientX, e.clientY, e.shiftKey);
});

document.addEventListener('keydown', (e) => {
  // While settings are open they own the keyboard, or typing would leak through
  // to the search box behind the panel.
  if (el.setwrap.classList.contains('on')) {
    if (e.key === 'Escape') { e.preventDefault(); closeSettings(); }
    return;
  }

  if (e.key === ',' && e.ctrlKey) { e.preventDefault(); openSettings(); return; }

  if (e.key === 'Escape') {
    if (el.menu.classList.contains('on')) { closeMenu(); return; }
    if (el.q.value) { el.q.value = ''; state.query = ''; el.clear.classList.remove('on'); runQuery(); }
    else host.postMessage({ cmd: 'window', action: 'close' });
    return;
  }

  if (e.key === 'ArrowDown')      { e.preventDefault(); select(state.selected + 1, true); }
  else if (e.key === 'ArrowUp')   { e.preventDefault(); select(state.selected - 1, true); }
  else if (e.key === 'PageDown')  { e.preventDefault(); select(state.selected + Math.floor(el.scroller.clientHeight / ROW_H), true); }
  else if (e.key === 'PageUp')    { e.preventDefault(); select(state.selected - Math.floor(el.scroller.clientHeight / ROW_H), true); }
  else if (e.key === 'Home' && e.ctrlKey) { e.preventDefault(); select(0, true); }
  else if (e.key === 'End'  && e.ctrlKey) { e.preventDefault(); select(state.total - 1, true); }
  else if (e.key === 'Enter') {
    e.preventDefault();
    const row = selectedRow() || rowAt(0);
    if (row) act(e.ctrlKey || e.shiftKey ? 'reveal' : 'open', row);
  }
  else if (e.key === 'c' && e.ctrlKey) {
    const row = selectedRow();
    if (row && document.activeElement !== el.q) { e.preventDefault(); act('copyPath', row); }
  }
  else if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) {
    e.preventDefault();
    openMenuForSelection(e.shiftKey && e.key === 'ContextMenu');
  }
  else if (e.key === 'F5') { e.preventDefault(); startReindex(); }
  else if (e.key === 'l' && e.ctrlKey) { e.preventDefault(); focusSearch(); }
  else if (e.key === 'f' && e.ctrlKey) { e.preventDefault(); focusSearch(); }
  else if (!e.ctrlKey && !e.altKey && e.key.length === 1 && document.activeElement !== el.q) {
    focusSearch();
  }
});

function focusSearch() {
  el.q.focus();
  el.q.select();
}

function setQuery(text, types, tidy) {
  el.q.value = text || '';
  state.query = el.q.value;
  el.clear.classList.toggle('on', state.query.length > 0);

  if (types) {
    state.types = types;
    document.querySelectorAll('.chip[data-filter="types"]').forEach((c) => {
      c.classList.toggle('on', c.dataset.value === types);
    });
  }

  if (tidy) {
    state.tidy = true;
    document.querySelector('.chip[data-filter="tidy"]')?.classList.add('on');
  }

  runQuery();
  focusSearch();
}

/* window chrome ---------------------------------------------------------- */

/* The whole masthead is draggable except the controls sitting on it. */
const topBar = document.getElementById('top');
const onChrome = (e) => !e.target.closest('button, input, #searchwrap, .chips');

topBar.addEventListener('mousedown', (e) => {
  if (e.button !== 0 || !onChrome(e)) return;
  host.postMessage({ cmd: 'window', action: 'drag' });
});

topBar.addEventListener('dblclick', (e) => {
  if (!onChrome(e)) return;
  host.postMessage({ cmd: 'window', action: 'maximize' });
});

document.querySelectorAll('.winbtn').forEach((b) => {
  b.addEventListener('click', () => host.postMessage({ cmd: 'window', action: b.dataset.win }));
});

/* filter chips ------------------------------------------------------------ */

document.querySelectorAll('.chip').forEach((chip) => {
  chip.addEventListener('click', () => {
    const { filter, value } = chip.dataset;

    if (filter === 'types') {
      const on = state.types === value;
      state.types = on ? 'all' : value;
      document.querySelectorAll('.chip[data-filter="types"]').forEach((c) => {
        c.classList.toggle('on', !on && c.dataset.value === value);
      });
    } else if (filter === 'mode') {
      const on = state.mode === value;
      state.mode = on ? 'smart' : value;
      chip.classList.toggle('on', !on);
    } else if (filter === 'tidy') {
      state.tidy = !state.tidy;
      chip.classList.toggle('on', state.tidy);
    }
    runQuery();
    el.q.focus();
  });
});

/* context menu ------------------------------------------------------------
   The entries come from Explorer's own IContextMenu for the file, so whatever
   is installed — 7-Zip, an editor, a shell extension — appears here exactly as
   it would in a folder window. We only do the drawing. */

const menuState = { seq: 0, at: { x: 0, y: 0 }, submenus: [] };

function requestMenu(x, y, extended) {
  menuState.at = { x, y };
  const row = selectedRow();
  if (!row) return;

  // Show immediately with a placeholder; shell extensions can take a beat to load
  // the first time a file type is used.
  el.menu.innerHTML = '<div class="empty">Loading…</div>';
  placeMenu(el.menu, x, y);
  el.menu.classList.add('on');

  host.postMessage({ cmd: 'shellMenu', id: ++menuState.seq, path: row.p, extended: !!extended });
}

/* Used by --open-menu, so the context menu can be exercised without needing the
   window to hold focus. Waits for results before opening. */
function openMenuWhenReady(tries = 0) {
  if (state.total === 0 && tries < 60) {
    setTimeout(() => openMenuWhenReady(tries + 1), 250);
    return;
  }
  select(0, false);
  setTimeout(() => openMenuForSelection(false), 60);
}

/* Keyboard route to the same menu, anchored under the selected row. */
function openMenuForSelection(extended) {
  if (state.selected < 0) select(0, true);
  const node = state.nodes.get(state.selected);
  if (!node) return;
  const r = node.getBoundingClientRect();
  requestMenu(r.left + 56, r.top + r.height - 8, extended);
}

function onShellMenu(m) {
  if (m.id !== menuState.seq || !el.menu.classList.contains('on')) return;

  el.menu.replaceChildren();
  const items = m.items || [];

  if (items.length) {
    renderMenuInto(el.menu, items);
    el.menu.appendChild(document.createElement('hr'));
  }

  // Lighthouse's own additions, below whatever Windows offered.
  for (const [act, label] of [
    ['openFolder', 'Open containing folder'],
    ['copyPath',   'Copy full path'],
    ['copyName',   'Copy name'],
  ]) {
    el.menu.appendChild(buildMenuButton({ label }, () => { act1(act); }));
  }

  placeMenu(el.menu, menuState.at.x, menuState.at.y);
}

function act1(action) { act(action, selectedRow()); closeMenu(); }

function renderMenuInto(container, items) {
  for (const item of items) {
    if (item.sep) { container.appendChild(document.createElement('hr')); continue; }

    const btn = buildMenuButton(item, item.children
      ? null
      : () => { host.postMessage({ cmd: 'shellInvoke', menuId: item.id }); closeMenu(); });

    if (item.children) attachSubmenu(btn, item.children);
    container.appendChild(btn);
  }
}

function buildMenuButton(item, onClick) {
  const btn = document.createElement('button');
  if (item.disabled) btn.disabled = true;
  if (item.default) btn.classList.add('default');

  const left = document.createElement('span');
  if (item.icon) {
    const img = document.createElement('img');
    img.className = 'mi';
    img.src = item.icon;
    img.alt = '';
    left.appendChild(img);
  } else if (item.checked) {
    left.className = 'mtick';
    left.textContent = '✓';
  }

  const label = document.createElement('span');
  label.className = 'mlabel';
  label.textContent = item.label;

  const right = document.createElement('span');
  right.className = 'marrow';
  if (item.children) right.textContent = '▶';

  btn.append(left, label, right);
  if (onClick && !item.disabled) btn.addEventListener('click', onClick);
  return btn;
}

function attachSubmenu(btn, children) {
  let panel = null;

  const open = () => {
    if (panel) return;
    panel = document.createElement('div');
    panel.className = 'ctxmenu on';
    renderMenuInto(panel, children);
    document.body.appendChild(panel);
    menuState.submenus.push(panel);

    const r = btn.getBoundingClientRect();
    placeMenu(panel, r.right - 4, r.top - 6);
    btn.classList.add('open');
  };

  const close = () => {
    if (!panel) return;
    panel.remove();
    menuState.submenus = menuState.submenus.filter((p) => p !== panel);
    panel = null;
    btn.classList.remove('open');
  };

  let timer = 0;
  btn.addEventListener('mouseenter', () => { clearTimeout(timer); open(); });
  btn.addEventListener('mouseleave', () => {
    timer = setTimeout(() => {
      if (panel && !panel.matches(':hover')) close();
    }, 260);
  });
  btn.addEventListener('click', (e) => { e.stopPropagation(); open(); });
}

/* Keeps a panel fully on screen, flipping it left or upward near an edge. */
function placeMenu(panel, x, y) {
  panel.style.left = '0px';
  panel.style.top = '0px';
  const r = panel.getBoundingClientRect();

  let left = x;
  let top = y;
  if (left + r.width > window.innerWidth - 8) left = Math.max(8, x - r.width);
  if (top + r.height > window.innerHeight - 8) top = Math.max(8, window.innerHeight - r.height - 8);

  panel.style.left = left + 'px';
  panel.style.top = top + 'px';
}

function closeMenu() {
  el.menu.classList.remove('on');
  el.menu.replaceChildren();
  for (const p of menuState.submenus) p.remove();
  menuState.submenus = [];
  host.postMessage({ cmd: 'shellMenuClose' });
}

document.addEventListener('mousedown', (e) => {
  if (!e.target.closest('.ctxmenu')) closeMenu();
});

/* settings ----------------------------------------------------------------
   The host owns these: it applies the theme before the document loads and it
   decides what the close button does. This panel just reflects and edits them. */

function applySettings(s) {
  el.setTray.checked = !!s.closeToTray;
  el.setDark.checked = s.theme === 'dark';
  document.documentElement.dataset.theme = s.theme === 'dark' ? 'dark' : 'light';

  const close = document.querySelector('.winbtn.close');
  if (close) close.title = s.closeToTray ? 'Close to tray' : 'Close';
}

function pushSettings() {
  const theme = el.setDark.checked ? 'dark' : 'light';
  document.documentElement.dataset.theme = theme;

  const close = document.querySelector('.winbtn.close');
  if (close) close.title = el.setTray.checked ? 'Close to tray' : 'Close';

  host.postMessage({ cmd: 'saveSettings', closeToTray: el.setTray.checked, theme });
}

function openSettings() {
  el.setwrap.classList.add('on');
  el.setClose.focus();
}

function closeSettings() {
  el.setwrap.classList.remove('on');
  focusSearch();
}

el.gear.addEventListener('click', openSettings);
el.setClose.addEventListener('click', closeSettings);
el.setTray.addEventListener('change', pushSettings);
el.setDark.addEventListener('change', pushSettings);

// Clicking the dimmed surround closes; clicking the panel itself must not.
el.setwrap.addEventListener('mousedown', (e) => {
  if (e.target === el.setwrap) closeSettings();
});

/* boot -------------------------------------------------------------------- */

el.sizer.style.height = '0px';
focusSearch();
runQuery();

// Tell the host the message listener is live, so it can send startup state
// without racing this script.
host.postMessage({ cmd: 'ready' });
