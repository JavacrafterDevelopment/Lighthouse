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
};

const state = {
  query:    '',
  mode:     'smart',
  types:    'all',
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
};

/* ------------------------------------------------------------------ query */

function runQuery() {
  state.seq++;
  state.pages.clear();
  state.pending.clear();
  state.nodes.clear();
  el.rows.replaceChildren();
  state.selected = -1;
  state.total = 0;
  el.scroller.scrollTop = 0;
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
    hidden: true,
    system: false,
  });
}

host.addEventListener('message', (e) => {
  const m = e.data;
  if (m.evt === 'status')   return onStatus(m);
  if (m.evt === 'focus')    return focusSearch();
  if (m.evt === 'setQuery') return setQuery(m.query);
  if (typeof m.seq === 'number') return onResults(m);
});

function onResults(m) {
  if (m.seq !== state.seq) return;   // belongs to an older query

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
  el.lamp.className = m.phase === 'scanning' || m.phase === 'starting' ? 'scanning'
                    : m.phase === 'limited' ? 'limited'
                    : m.phase === 'failed'  ? '' : 'ready';

  // The index grows while scanning, so keep refreshing what is on screen.
  if (m.phase === 'scanning' || m.phase === 'ready' || m.phase === 'limited') runQuery();
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
  openMenu(e.clientX, e.clientY);
});

document.addEventListener('keydown', (e) => {
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

function setQuery(text) {
  el.q.value = text || '';
  state.query = el.q.value;
  el.clear.classList.toggle('on', state.query.length > 0);
  runQuery();
  focusSearch();
}

/* window chrome ---------------------------------------------------------- */

document.getElementById('titlebar').addEventListener('mousedown', (e) => {
  if (e.target.closest('.winbtn')) return;
  if (e.button !== 0) return;
  host.postMessage({ cmd: 'window', action: 'drag' });
});

document.getElementById('titlebar').addEventListener('dblclick', (e) => {
  if (e.target.closest('.winbtn')) return;
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
    }
    runQuery();
    el.q.focus();
  });
});

/* context menu ------------------------------------------------------------ */

function openMenu(x, y) {
  el.menu.classList.add('on');
  const r = el.menu.getBoundingClientRect();
  el.menu.style.left = Math.min(x, window.innerWidth  - r.width  - 8) + 'px';
  el.menu.style.top  = Math.min(y, window.innerHeight - r.height - 8) + 'px';
}

function closeMenu() { el.menu.classList.remove('on'); }

el.menu.addEventListener('click', (e) => {
  const btn = e.target.closest('button');
  if (!btn) return;
  act(btn.dataset.act, selectedRow());
  closeMenu();
});

document.addEventListener('mousedown', (e) => {
  if (!e.target.closest('#menu')) closeMenu();
});

/* boot -------------------------------------------------------------------- */

el.sizer.style.height = '0px';
focusSearch();
runQuery();
