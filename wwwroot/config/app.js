// Config page for the Edge Gateway. Plain JS, no libraries: it runs offline
// on the gateway PC and is served by AdminWebServer.

const FILES = {
  'point-map':       { restartToApply: false },
  'timestamp-rules': { restartToApply: false },
  'mqtt-config':     { restartToApply: true },
  'machine-config':  { restartToApply: true },
};
const TWIN_GROUPS = ['IO', 'MOTION']; // never sent to MES, so they need no timestamp rule
const ALL = '__all';
const UNID = '__unidentified';
const NEW = '__new';
const BLANK_ROWS = 3;

// Top tabs: General (gateway-wide settings), Other machines (the shared
// mapping), then one tab per machine with its own mapping.
const GENERAL = '__general';
const SHARED = '__shared';
const GENERAL_VIEWS = new Set(['mqtt-config', 'machine-config', 'machines', 'failed', 'log']);

const state = {
  tab: GENERAL,
  lastView: { general: 'machines', machine: 'point-map' }, // reopened when coming back to that kind of tab
  view: 'machines',         // opens on the overview: is it running, which machines, what's wrong
  data: {},                 // file key -> data as loaded / being edited
  saved: {},                // PointMap / TimeStamp as last loaded or saved: what a save changed is copied to other machines
  versions: {},             // file key -> its version (ETag) as loaded / saved: a save sends it back (If-Match)
  numberKeys: {},           // file key -> keys that must stay numbers (form views)
  dirty: new Set(),         // file keys with unsaved edits
  ruleOpen: new Set(),      // groups whose time rule is opened in their PointMap panel
  findMachine: '',          // the machine list's filter
  waiting: {}, waitingAt: {}, // outbox scope -> rows waiting for MES, and when that was read
  group: { 'point-map': null, 'timestamp-rules': null },
  extraGroups: [],          // made with "+ New group…" and not saved yet
  unidentified: [],         // from /api/unidentified
  unidEdits: {},            // tag -> { dataGroup, fieldName }
  search: '',
  status: null,
  restartToApply: false,
  logs: [],                 // log lines received so far (newest last)
  lastLogId: 0,
  errorCount: 0,            // problem lines since the gateway started
  seenErrors: 0,            // errorCount when the Log view was last opened
  seenLogId: 0,             // the last log line then (which problems are new, for "solved since")
  problems: { mes: new Map(), mqtt: null }, // ongoing: MES down per forwarder -> since, MQTT down since (banners)
  unreachable: false,       // the page lost its connection to the gateway
  errorsOnly: false,
  machines: null,           // /api/machines
  outbox: { scope: '', scopes: null, days: null, day: null, machine: '', group: '', source: '', status: '', rows: [], more: false, open: {}, loading: false },
  scope: '',                // PointMap / TimeStamp shown for: '' = shared, else a machine_id with its own mapping
  machineConfigs: [],       // machines with their own mapping: [{ machineId, readable, error }]
  mesSchema: {},            // the MES format: data group -> MES columns (Config/mes-schema.json, all machines)
  mesDirty: false,          // MES format edited (saved together with PointMap)
  held: [],                 // machines sending without a tab: their data is held (/api/held)
  unmapped: [],             // machines whose data is held for tags their mapping doesn't know (/api/unmapped)
  copyFrom: {},             // held machine -> mapping picked in "copy mapping from" (survives the 4 s redraw)
  failedCount: 0,           // messages MES rejected as invalid, waiting for re-send / discard (status.failed)
  failed: null,             // /api/failed: { total, rows } for the MES failures view
  failedPicked: new Set(),  // "scope|day|id" ticked in the MES failures view
  failedOpen: {},           // "scope|day|id" -> envelope JSON shown
};
const IGNORED = 'IGNORED';  // PointMap group for tags that are known but never sent

// Held-for-unmapped-tags entries whose mapping is the tab shown ('' = Template).
const unmappedFor = scope => state.unmapped.filter(u => (u.mapping ?? '') === scope);

// unregisteredMachines in GatewayConfig: "hold" (default) or "shared".
const holdPolicy = () => String(state.data['machine-config']?.unregisteredMachines ?? 'hold').trim().toLowerCase() !== 'shared';
// The shared mapping's tab: only a template for new machine tabs while machines without a tab are held.
const sharedLabel = () => (holdPolicy() ? 'Template' : 'Other machines');
const MAPPING = new Set(['point-map', 'timestamp-rules']); // the files a machine can have its own of

// "?machine=<id>" for the scoped files when a machine's own mapping is shown.
function scopeQuery(key) {
  return MAPPING.has(key) && state.scope ? `?machine=${encodeURIComponent(state.scope)}` : '';
}
const MAX_LOG_LINES = 500;

// One colour (app.css --g-<group>) and icon (index.html #g-<group>) per group; any other group shares one.
const KNOWN_GROUPS = new Set(['MACHINE_STATUS', 'PRODUCTION', 'PROCESS', 'QUALITY', 'TRACEABILITY', 'ALARM', 'OPERATOR', 'MATERIAL', 'MAINTENANCE', 'MOTION', 'IO']);
const gkey = group => (KNOWN_GROUPS.has(group) ? group : 'other');
const gcolor = group => `var(--g-${gkey(group)})`;
const SHORT_GROUP = { MACHINE_STATUS: 'STATUS', MAINTENANCE: 'MAINT.', TRACEABILITY: 'TRACE' };

// ---------- small helpers ----------

const $ = id => document.getElementById(id);

const SVG_NS = 'http://www.w3.org/2000/svg';
function icon(id) {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('class', 'i');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(SVG_NS, 'use');
  use.setAttribute('href', `#${id}`);
  svg.append(use);
  return svg;
}

// Segmented control: [[value, label], ...], one is on.
function seg(options, value, onChange, label) {
  return h('div', { class: 'seg', role: 'group', 'aria-label': label },
    ...options.map(([v, text]) => h('button', { type: 'button', class: v === value ? 'on' : '', 'aria-pressed': String(v === value), textContent: text, onclick: () => onChange(v) })));
}

// The view's own controls, left of the search box.
function setTools(...nodes) {
  $('tools').replaceChildren(...nodes.filter(Boolean));
}

const fmtMs = ms => (ms >= 1000 && ms % 1000 === 0 ? `${ms / 1000} s` : `${ms} ms`);

function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === 'class') el.className = value;
    else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
    else if (key in el) el[key] = value;
    else el.setAttribute(key, value);
  }
  for (const child of children) if (child != null) el.append(child);
  return el;
}

function select(options, value, onChange, label) {
  const el = h('select', { onchange: e => onChange(e.target.value), 'aria-label': label ?? '' },
    ...options.map(([v, text]) => h('option', { value: v, textContent: text })));
  el.value = value;
  return el;
}

// Our own suggestion list under a text box (the browser's datalist popup is grey and doesn't match the page).
// One list for the page, fixed to the box, so a scrolling table doesn't clip it.
// options(text) -> [{ value, text?, hint?, extra? } | { sep } | { note }]; color() -> the group colour, or nothing.
const comboList = h('ul', { class: 'combo-list', id: 'comboList', role: 'listbox', hidden: true });
document.body.append(comboList);
let comboClose = () => {};
document.addEventListener('scroll', e => { if (e.target !== comboList) comboClose(); }, true);
window.addEventListener('resize', () => comboClose());

function combo(input, options, color) {
  let items = [], active = -1;
  Object.entries({ role: 'combobox', 'aria-autocomplete': 'list', 'aria-controls': 'comboList', 'aria-expanded': 'false', autocomplete: 'off' })
    .forEach(([k, v]) => input.setAttribute(k, v));
  const close = () => {
    comboList.hidden = true;
    input.setAttribute('aria-expanded', 'false');
    input.removeAttribute('aria-activedescendant');
    active = -1;
  };
  const pick = value => {
    input.value = value;
    close();
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
  };
  const mark = i => {
    active = i;
    comboList.querySelectorAll('[role=option]').forEach((li, n) => { li.classList.toggle('on', n === i); li.setAttribute('aria-selected', String(n === i)); });
    const li = $(`combo-${i}`);
    if (li) { input.setAttribute('aria-activedescendant', li.id); li.scrollIntoView({ block: 'nearest' }); }
  };
  const open = () => {
    const all = options(input.value.trim());
    items = all.filter(o => 'value' in o);
    if (!all.length) { close(); return; }
    let n = 0;
    comboList.replaceChildren(...all.map(o =>
      o.sep ? h('li', { class: 'sep', role: 'presentation', textContent: o.sep })
      : o.note ? h('li', { class: 'note', role: 'presentation', textContent: o.note })
      : h('li', { id: `combo-${n++}`, role: 'option', 'aria-selected': 'false', class: o.extra ? 'extra' : '',
          onmousedown: e => { e.preventDefault(); pick(o.value); } }, // keeps the focus in the box
          h('span', { textContent: o.text ?? o.value }), o.hint ? h('span', { class: 'hint', textContent: o.hint }) : null)));
    comboList.style.setProperty('--c', color?.() || 'var(--text)');
    const r = input.getBoundingClientRect();
    const below = innerHeight - r.bottom > 220 || r.top < 220;
    Object.assign(comboList.style, { left: `${r.left}px`, minWidth: `${r.width}px`,
      top: below ? `${r.bottom + 4}px` : 'auto', bottom: below ? 'auto' : `${innerHeight - r.top + 4}px` });
    comboList.hidden = false;
    input.setAttribute('aria-expanded', 'true');
    comboClose = close;
    active = -1;
  };
  input.addEventListener('focus', open);
  input.addEventListener('input', e => { if (e.isTrusted) open(); });
  input.addEventListener('blur', close);
  input.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (comboList.hidden) open();
      if (items.length) mark((active + (e.key === 'ArrowDown' ? 1 : items.length - 1 + (active < 0 ? 1 : 0))) % items.length);
    } else if (e.key === 'Enter' && !comboList.hidden && active >= 0) {
      e.preventDefault();
      pick(items[active].value);
    } else if (e.key === 'Escape' && !comboList.hidden) {
      e.preventDefault();
      close();
    }
  });
  return input;
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

let toastTimer;
function toast(message, bad = false) {
  const el = $('toast');
  el.textContent = message;
  el.classList.toggle('bad', bad);
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), bad ? 6000 : 3000);
}

async function request(url, options) {
  // The server takes changes only as JSON (#12), also POST/DELETE without a body.
  return (await requestVersioned(url, options)).body;
}

// Also the version (ETag) the gateway sent with a config file. A failure carries .status (409 = the
// file changed since the copy this save came from).
async function requestVersioned(url, options) {
  const res = await fetch(url, { cache: 'no-store', ...options, headers: { 'Content-Type': 'application/json', ...options?.headers } });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw Object.assign(new Error(body.error || `HTTP ${res.status}`), { status: res.status });
  return { body, version: res.headers.get('ETag') };
}

// Saves a config file as the copy its version came from; null = no check.
const ifMatch = version => version ? { 'If-Match': version } : {};

function matches(text) {
  const q = state.search.trim().toLowerCase();
  return !q || text.toLowerCase().includes(q);
}

function markDirty(key) {
  state.dirty.add(key);
  updateNav();
}

// ---------- loading ----------

async function loadAll() {
  await loadMachineConfigs();
  for (const key of Object.keys(FILES))
    await loadFile(key);
  await loadMesSchema();
  state.dirty.clear();
  await loadUnidentified();
}

async function loadMesSchema() {
  try { state.mesSchema = await request('/api/config/mes-schema'); }
  catch { state.mesSchema = {}; }
  state.mesDirty = false;
}

async function loadFile(key) {
  const { body: data, version } = await requestVersioned(`/api/config/${key}${scopeQuery(key)}`);
  state.data[key] = data;
  state.versions[key] = version;
  if (MAPPING.has(key)) state.saved[key] = structuredClone(data);
  if (!Array.isArray(data))
    state.numberKeys[key] = Object.entries(data).filter(([, v]) => typeof v === 'number').map(([k]) => k);
}

async function loadUnidentified() {
  try { state.unidentified = await request(`/api/unidentified${scopeQuery('point-map')}`); }
  catch { state.unidentified = []; }
}

async function loadMachineConfigs() {
  // A failed request keeps the list: it must not look like every tab was closed.
  try { state.machineConfigs = await request('/api/machine-configs'); }
  catch { renderTabs(); return; }
  if (state.scope && !state.machineConfigs.some(m => m.machineId === state.scope)) {
    // The shown machine's tab is gone (closed here or in another window): load the
    // Template's own data, so a save can't write that machine's mapping over it (#9).
    for (const key of MAPPING) state.dirty.delete(key);
    await loadScope('');
    if (state.tab !== GENERAL) { state.tab = SHARED; resetOutbox(); }
    render();
    return;
  }
  renderTabs();
}

// Loads the shared mapping (scope '') or one machine's own: PointMap, TimeStamp,
// Unidentified. false = the user kept unsaved changes instead.
async function loadScope(scope) {
  const unsaved = [...MAPPING].filter(k => state.dirty.has(k));
  if (unsaved.length && !confirm('Unsaved changes in ' + unsaved.join(' and ') + ' will be lost. Switch anyway?'))
    return false;
  state.scope = scope;
  state.group = { 'point-map': null, 'timestamp-rules': null };
  state.extraGroups = [];
  state.unidEdits = {};
  try {
    for (const key of MAPPING) { await loadFile(key); state.dirty.delete(key); }
    if (state.mesDirty) await loadMesSchema(); // unsaved MES format edits are dropped too
    await loadUnidentified();
  } catch (e) {
    toast(`Could not load that mapping: ${e.message}`, true);
  }
  return true;
}

const isMachineTab = tab => tab !== GENERAL;
const scopeOfTab = tab => (tab === SHARED || tab === GENERAL ? '' : tab);

async function switchTab(tab) {
  if (tab === state.tab) return;
  if (isMachineTab(tab) && scopeOfTab(tab) !== state.scope && !(await loadScope(scopeOfTab(tab))))
    return;
  state.lastView[isMachineTab(state.tab) ? 'machine' : 'general'] = state.view;
  state.tab = tab;
  state.view = isMachineTab(tab) ? state.lastView.machine : state.lastView.general;
  resetOutbox(); // the Outbox view shows this tab's own outbox
  render();
}

// "+" tab: a machine gets its own mapping, copied from the tab shown (or the shared one).
async function addMachine() {
  const fromScope = isMachineTab(state.tab) ? state.scope : '';
  const from = fromScope || `the ${sharedLabel()} mapping`;
  const id = (prompt(`machine_id that gets its own tab and mapping (it starts as a copy of ${from}):`) ?? '').trim();
  if (!id) return;
  try {
    await request('/api/machine-configs', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ machineId: id, copyFrom: fromScope || null }),
    });
  } catch (e) {
    toast(`Not created: ${e.message}`, true);
    return;
  }
  await loadMachineConfigs();
  toast(`${id} has its own mapping now (copied from ${from}). Its data uses it from its next message.`);
  await switchTab(id);
}

// "×" on a machine tab: back to the shared mapping.
async function removeMachine(id) {
  const after = holdPolicy() ? `its data is held until it gets a tab again` : `it goes back to the shared mapping (Other machines)`;
  if (!confirm(`Close the ${id} tab? ${after}. Its own files are kept in Config/machines-removed and its outbox is still delivered.`)) return;
  try {
    await request(`/api/machine-configs?machine=${encodeURIComponent(id)}`, { method: 'DELETE' });
  } catch (e) {
    toast(`Not removed: ${e.message}`, true);
    return;
  }
  await loadMachineConfigs(); // if it was the tab shown, this loads the Template's data
  toast(holdPolicy() ? `${id}'s tab is closed: its data is held until it gets a tab again.` : `${id} uses the shared mapping again.`);
}

// The machine's connection, as on the Machines view: connected / idle (no message for 60 s) /
// disconnected (also: never seen since the gateway started).
const STATUS_WORDS = { connected: 'sending', idle: 'connected, nothing sent for 60 s', disconnected: 'not connected' };
// One machine count for the whole page: every machine tab, plus machines sending without one.
function machineCount() {
  const ids = new Map(state.machineConfigs.map(m => [m.machineId.toLowerCase(), m.machineId]));
  for (const m of state.machines?.machines ?? []) ids.set(m.machineId.toLowerCase(), m.machineId);
  const all = [...ids.values()];
  return { all: all.length, online: all.filter(id => connectionOf(id) !== 'disconnected').length };
}

function connectionOf(machineId) {
  const m = state.machines?.machines?.find(x => x.machineId.toLowerCase() === machineId.toLowerCase());
  return m?.status in STATUS_WORDS ? m.status : 'disconnected';
}

function renderTabs() {
  // The machine shown in the work area; stays marked while a General view is open.
  const selected = state.tab === GENERAL ? (state.scope || SHARED) : state.tab;
  const tab = (id, label, opts = {}) => {
    const el = h('div', {
      class: `tab${selected === id ? ` active${state.tab === GENERAL ? ' parked' : ''}` : ''}${opts.fixed ? ' fixed' : ''}${opts.extra ? ' ' + opts.extra : ''}`,
      role: 'tab', tabIndex: 0, title: opts.title ?? label,
      onclick: () => switchTab(id),
      onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); switchTab(id); } },
    });
    el.setAttribute('aria-selected', String(selected === id));
    if (opts.status)
      el.append(h('span', { class: `dot ${opts.status}`, role: 'img', 'aria-label': STATUS_WORDS[opts.status], title: STATUS_WORDS[opts.status] }));
    else if (opts.fixed)
      el.append(icon('i-copy'));
    if (opts.extra === 'broken') el.append(icon('i-info'));
    el.append(h('span', { class: 'tab-name', textContent: label }));
    if (opts.auto)
      el.append(h('span', { class: 'tab-auto', textContent: 'auto', title: 'Tab created automatically: check it, then confirm it on its PointMap (counts as confirmed after 3 days)' }));
    if (opts.unid)
      el.append(h('span', { class: 'pill count', textContent: String(opts.unid), title: `${opts.unid} unidentified tag(s)` }));
    if (opts.close)
      el.append(h('button', {
        type: 'button', class: 'tab-x',
        title: holdPolicy() ? `Close: ${label}'s data is held until it gets a tab again` : `Close: ${label} goes back to the shared mapping`,
        'aria-label': `Close ${label}`, onclick: e => { e.stopPropagation(); removeMachine(id); },
      }, icon('i-close')));
    return el;
  };

  $('tabs').replaceChildren(...[
    tab(SHARED, sharedLabel(), { fixed: true, unid: state.scope ? state.status?.unidentified?.[''] ?? 0 : unidentifiedList().length, extra: unmappedFor('').length ? 'broken' : '', title: holdPolicy() ? 'Template: what "+ Add a machine" copies into a new machine tab. Machines without a tab are held, not mapped with this.' : 'The shared mapping: every machine that has no own tab' }),
    ...state.machineConfigs.filter(m => m.machineId.toLowerCase().includes(state.findMachine.trim().toLowerCase())).map(m => {
      const waiting = unmappedFor(m.machineId);
      const alert = !m.readable || waiting.length > 0;
      const status = connectionOf(m.machineId);
      return tab(m.machineId, m.machineId, {
        close: true, extra: alert ? 'broken' : '', status, auto: m.autoCopiedFrom != null,
        unid: m.machineId === state.scope ? unidentifiedList().length : state.status?.unidentified?.[m.machineId] ?? 0,
        title: !m.readable ? `${m.machineId}: its files can't be read (${m.error})`
          : waiting.length ? `${m.machineId}: data held, tags not mapped yet: ${[...new Set(waiting.flatMap(u => u.tags))].join(', ')}`
          : `${m.machineId} (${STATUS_WORDS[status]}): its own mapping and records sent to MES`,
      });
    }),
  ]);

  // Pinned under the list (always in view): add a machine, and what needs a person.
  $('mfoot').replaceChildren(...[
    h('button', { type: 'button', class: 'btn ghost tab-add', title: 'Give a machine its own tab and mapping', onclick: addMachine }, icon('i-plus'), 'Add a machine'),
    // Machines sending without a tab: their data waits (held) until someone registers them.
    state.held.length ? h('button', {
      type: 'button', class: 'tab-alert', textContent: `${state.held.length} not registered: data held`,
      title: `Sending without a tab, data held: ${state.held.map(m => m.machineId).join(', ')}. Click to register.`,
      onclick: () => switchView('machines'),
    }) : null,
    // Messages MES rejected as invalid: kept, not retried, until someone re-sends or discards them.
    state.failedCount ? h('button', {
      type: 'button', class: 'tab-alert', textContent: `${state.failedCount.toLocaleString()} rejected by MES`,
      title: 'MES said these messages are invalid. They are kept (not retried) until you re-send or discard them. Click to see why.',
      onclick: () => switchView('failed'),
    }) : null,
  ].filter(Boolean));

  const count = machineCount();
  $('machineCount').textContent = count.all ? `${count.online} of ${count.all} connected` : '';
  const failed = $('failedPill');
  failed.textContent = state.failedCount.toLocaleString();
  failed.className = state.failedCount ? 'pill count' : 'pill quiet';
}

// "register" on a held machine: it gets its own tab (copied from copyFrom) and
// its adapter is reconnected so it sends the held data again.
async function registerMachine(id, copyFrom) {
  let result;
  try {
    result = await request('/api/machine-configs', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ machineId: id, copyFrom: copyFrom || null }),
    });
  } catch (e) {
    toast(`Not registered: ${e.message}`, true);
    return;
  }
  await loadMachineConfigs();
  await loadHeld();
  toast(result.reconnected
    ? `${id} registered. Its adapter was reconnected and sends the held data again. Map the tags it sent on its Unidentified page.`
    : `${id} registered. Its held data arrives with its adapter's next resend. Map the tags it sent on its Unidentified page.`);
  await switchTab(id);
  state.group['point-map'] = UNID; // the tags it sent, to map
  switchView('point-map');
}

async function loadHeld() {
  try { state.held = await request('/api/held'); } catch { state.held = []; }
  renderTabs();
}

async function loadUnmapped() {
  const before = JSON.stringify(state.unmapped);
  try { state.unmapped = await request('/api/unmapped'); } catch { state.unmapped = []; }
  if (JSON.stringify(state.unmapped) !== before) {
    renderTabs();
    renderScopeHint();
  }
}

// Red bar on a mapping tab: its mapping can't be read, or messages are held
// for tags it doesn't know yet.
function renderScopeHint() {
  const hint = $('scopeHint');
  const machineTab = isMachineTab(state.tab);
  const broken = machineTab && state.machineConfigs.find(m => m.machineId === state.scope && !m.readable);
  const waiting = machineTab ? unmappedFor(state.scope) : [];
  const auto = machineTab && state.tab !== SHARED ? state.machineConfigs.find(m => m.machineId === state.scope && m.autoCopiedFrom != null) : null;
  hint.replaceChildren();
  hint.classList.toggle('info', !!auto && !broken && !waiting.length);
  hint.hidden = !broken && !waiting.length && !auto;
  if (auto && !broken && !waiting.length) {
    hint.append(icon('i-info'),
      `${auto.machineId}'s tab was created automatically, as a copy of ${auto.autoCopiedFrom || 'the Template'}. Check its mapping, then confirm it or pick another. Left as it is, it counts as confirmed 3 days after it was created.`,
      h('button', { type: 'button', class: 'btn small ok', textContent: 'Confirm', onclick: () => confirmAutoTab(auto.machineId) }),
      h('button', { type: 'button', class: 'btn small warnb', textContent: 'Cancel', title: 'Pick another mapping for it instead', onclick: () => reassignTab(auto.machineId) }));
    return;
  }
  if (broken) {
    hint.append(icon('i-info'), `${broken.machineId}'s files can't be read, so its lines are held until they are fixed: ${broken.error}`);
    return;
  }
  if (!waiting.length) return;
  const tags = [...new Set(waiting.flatMap(u => u.tags))];
  const lines = waiting.reduce((n, u) => n + u.lines, 0);
  hint.append(
    `Held: ${lines.toLocaleString()} message(s) from ${waiting.map(u => u.machineId).join(', ')}, because this mapping doesn't know: ${tags.join(', ')}. ` +
    'Map each one or set it to ignore, then save: the held messages are sent again. ',
    h('button', { type: 'button', class: 'link', textContent: 'show them', onclick: () => { state.group['point-map'] = UNID; switchView('point-map'); } }));
  hint.prepend(icon('i-info'));
}

async function confirmAutoTab(id) {
  try { await request(`/api/machine-configs/confirm?machine=${encodeURIComponent(id)}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' }); }
  catch (e) { toast(`Not confirmed: ${e.message}`, true); return; }
  await loadMachineConfigs();
  render();
  toast(`${id}'s tab is confirmed.`);
}

// Another mapping for a machine tab: "✗ cancel" on an automatic tab (#30), "Change mapping" next to Save on a machine tab (#31).
// Its mapping becomes a copy of another tab's or the Template's (the old files are kept as .bak).
// A confirmed tab asks for the password when one is set (else just "are you sure");
// its data keeps the old mapping until then.
async function reassignTab(id) {
  const auto = state.machineConfigs.find(m => m.machineId === id)?.autoCopiedFrom != null;
  const others = state.machineConfigs.filter(c => c.readable && c.machineId !== id).map(c => c.machineId);
  const guess = guessCopyFrom(id);
  const pick = prompt(`Copy which mapping into ${id}? A tab (${others.join(', ') || 'none'}), or empty for the ${sharedLabel()}.`, guess === id ? '' : guess);
  if (pick === null) return;
  const from = pick.trim();
  if (from && !others.some(o => o.toLowerCase() === from.toLowerCase())) { toast(`Not changed: ${from} is not a tab.`, true); return; }
  let password = '';
  if (!auto && state.status?.mappingPasswordSet !== false) { // unknown status: ask, the gateway decides
    password = prompt(`${id}'s tab is confirmed. Password to change its mapping:`) ?? '';
    if (!password) return;
  } else if (!auto && !confirm(`${id}'s tab is confirmed. Replace its mapping with a copy of ${from || `the ${sharedLabel()}`}? (The old files are kept as .bak.)`)) return;
  if (state.scope === id && [...MAPPING].some(k => state.dirty.has(k)) && !confirm(`Unsaved changes on ${id} will be lost. Go on?`)) return;
  try {
    await request(`/api/machine-configs/reassign?machine=${encodeURIComponent(id)}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ copyFrom: from || null, password }),
    });
  } catch (e) { toast(`Not changed: ${e.message}`, true); return; }
  if (state.scope === id) { for (const key of MAPPING) state.dirty.delete(key); await loadScope(id); }
  await loadMachineConfigs();
  render();
  toast(`${id} uses a copy of ${from || `the ${sharedLabel()}`} now (old files kept as .bak).`);
}

function pointMapGroups() {
  const groups = [];
  const add = g => { if (g && !groups.includes(g)) groups.push(g); };
  Object.keys(state.mesSchema ?? {}).forEach(add); // every MES table, even with nothing mapped yet
  state.data['point-map'].forEach(row => add(row.dataGroup));
  Object.keys(state.data['timestamp-rules'] ?? {}).forEach(add);
  state.extraGroups.forEach(add);
  return groups;
}

function unidentifiedList() {
  // A joined tag (line_no+process_id) maps each of its parts too.
  const mapped = new Set((state.data['point-map'] ?? []).flatMap(row => (row.tag ?? '').toLowerCase().split('+').map(t => t.trim())));
  return state.unidentified.filter(u => !mapped.has(u.tag.toLowerCase()));
}

// ---------- rendering ----------

// The General tabs in the top bar, and the machine's Mapping | Outbox bar (with unsaved marks).
function updateNav() {
  document.querySelectorAll('.nav').forEach(button => {
    button.classList.toggle('active', button.dataset.view === state.view);
    button.querySelector('.dirty')?.remove();
    if (state.dirty.has(button.dataset.view))
      button.append(h('span', { class: 'dirty', role: 'img', 'aria-label': 'unsaved changes', title: 'Unsaved changes' }));
  });
  renderBanner();
}

const GENERAL_TITLES = {
  'mqtt-config': ['i-broker', 'MQTT broker'], 'machine-config': ['i-sliders', 'Gateway settings'],
  machines: ['i-robot', 'Machines'], failed: ['i-x', 'MES failures'], log: ['i-scroll', 'Log'],
};

function renderBanner() {
  const banner = $('banner');
  if (!isMachineTab(state.tab)) {
    const [ic, title] = GENERAL_TITLES[state.view] ?? ['i-sliders', state.view];
    banner.replaceChildren(h('div', { class: 'split one' }, h('div', { class: 'on' }, icon(ic), `General · ${title}`)));
    return;
  }
  const name = state.scope || sharedLabel();
  const mapping = state.view !== 'outbox';
  const unidView = state.view === 'point-map' && state.group['point-map'] === UNID;
  const dirty = key => state.dirty.has(key) ? h('span', { class: 'dirty', role: 'img', 'aria-label': 'unsaved changes', title: 'Unsaved changes' }) : null;
  const unid = unidentifiedList().length;
  const viewButton = (on, ic, label, onclick, extra) => h('button', { type: 'button', class: on ? 'on' : '', 'aria-pressed': String(on), onclick }, icon(ic), label, extra);
  banner.replaceChildren(
    h('div', { class: 'split' },
      h('button', { type: 'button', class: mapping ? 'on' : '', onclick: () => switchView(state.lastMapping ?? 'point-map') }, icon('i-map'), `${name} Mapping`),
      h('button', { type: 'button', class: mapping ? '' : 'on', onclick: () => switchView('outbox') }, icon('i-tray'), 'Sent to MES')),
    mapping
      ? h('div', { class: 'views' },
          viewButton(state.view === 'point-map' && !unidView, 'i-map', 'PointMap', () => { state.group['point-map'] = ALL; switchView('point-map'); }, dirty('point-map') ?? dirty('timestamp-rules')),
          viewButton(unidView, 'i-question', 'Unidentified', async () => { state.group['point-map'] = UNID; await loadUnidentified(); switchView('point-map'); },
            h('span', { class: unid ? 'pill count' : 'pill quiet', id: 'unidCount', textContent: String(unid) })))
      : h('div', { class: 'views' }, h('button', { type: 'button', class: 'on' }, icon('i-tray'), 'Records: sent, waiting, rejected')));
}

// Left of the work area on a machine tab: who it is, how it is doing, what it sends live.
function renderProfile() {
  const el = $('profile');
  const show = isMachineTab(state.tab) && state.data['point-map'];
  el.hidden = !show;
  $('work').classList.toggle('with-profile', !!show);
  if (!show) return;

  const id = state.scope;
  const cfg = state.machineConfigs.find(m => m.machineId === id);
  const live = id ? state.machines?.machines?.find(x => x.machineId.toLowerCase() === id.toLowerCase()) : null;
  const status = id ? connectionOf(id) : null;
  const rows = state.data['point-map'];
  const mapped = rows.filter(r => (r.tag ?? '').trim() && r.dataGroup !== IGNORED && r.dataGroup !== 'UNIDENTIFIED').length;
  const fills = pointMapGroups().filter(g => mesColumns(g)).map(groupFill);
  const filled = fills.reduce((n, f) => n + f.tags + f.gw, 0), total = fills.reduce((n, f) => n + f.total, 0);
  const unid = unidentifiedList().length;
  const auto = cfg?.autoCopiedFrom != null;
  const row = (ic, label, value, style) => h('li', {}, icon(ic), h('span', { textContent: label }), h('span', { class: 'v', textContent: value, title: String(value), ...(style ? { style } : {}) }));

  const portrait = h('section', { class: 'card portrait' },
    h('div', { class: 'plate' },
      h('span', { class: `lvl${auto ? ' auto' : ''}`, textContent: !id ? 'template' : auto ? 'auto' : 'confirmed' }),
      icon(id ? 'i-robot' : 'i-copy')),
    h('h2', { textContent: id || sharedLabel() }),
    id ? h('div', { class: 'sub' }, h('span', { class: `dot ${status}` }),
          `${STATUS_WORDS[status][0].toUpperCase()}${STATUS_WORDS[status].slice(1)}${live?.lastMessage ? ` · last line ${ago(live.lastMessage)}` : ''}`)
       : h('div', { class: 'sub', textContent: holdPolicy() ? 'Copied into new machine tabs' : 'Every machine without its own tab' }),
    id ? h('button', { type: 'button', class: 'btn ghost', onclick: () => reassignTab(id) }, icon('i-swap'), 'Change mapping') : null);

  const stats = h('section', { class: 'card' }, h('div', { class: 'head' }, icon('i-pulse'), id ? 'Machine' : 'Mapping'),
    h('ul', { class: 'rows' }, ...[
      id ? row('i-plug', 'Connection', live?.connection ?? '—', status === 'connected' ? 'color:var(--ok-text)' : null) : null,
      id ? row('i-tag', 'Data source', live?.dataSources?.map(d => d.name).join(', ') || '—') : null,
      id ? row('i-pulse', 'Lines', live ? `${live.total.toLocaleString()} · ${live.perSecond.toFixed(1)} / s` : '0') : null,
      auto ? row('i-copy', 'Copied from', cfg.autoCopiedFrom || 'Template') : null,
      row('i-tag', 'Tags mapped', mapped.toLocaleString()),
      row('i-map', 'MES fields filled', `${filled} / ${total}`, 'color:var(--accent)'),
      row('i-db', 'Waiting for MES', state.waiting[id || '']?.toLocaleString() ?? '—', state.waiting[id || ''] ? 'color:var(--warn-text)' : null),
      row('i-question', 'Unidentified tags', String(unid), unid ? 'color:var(--bad-text)' : null),
      live?.held ? row('i-db', 'Held', live.held.toLocaleString(), 'color:var(--bad-text)') : null,
      id ? row('i-clock', 'Time warnings (10 min)', String(live?.timeWarnings ?? 0), live?.timeWarnings ? 'color:var(--warn-text)' : null) : null,
      id ? row('i-copy', 'Repeats (10 min)', String(live?.repeats ?? 0), live?.repeats ? 'color:var(--warn-text)' : null) : null,
    ].filter(Boolean)));

  const twin = rows.filter(r => TWIN_GROUPS.includes(r.dataGroup) && (r.tag ?? '').trim());
  const twinCard = h('section', { class: 'card' },
    h('div', { class: 'head' }, icon('i-wave'), 'Twin (MQTT)', h('span', { class: 'badge quiet', textContent: 'live only' })),
    h('div', { class: 'chips' }, ...(twin.length
      ? twin.map(r => h('button', { type: 'button', class: 'chip', textContent: r.tag, title: `${r.dataGroup} / ${r.fieldName}: published live, never sent to MES`,
          onclick: () => { state.group['point-map'] = ALL; state.onlyGroup = null; state.focusGroup = r.dataGroup; switchView('point-map'); } }))
      : [h('span', { class: 'empty', textContent: 'No live data for the twin.' })])));

  el.replaceChildren(portrait, stats, twinCard);
}

function render() {
  if (state.view === 'timestamp-rules') state.view = 'point-map'; // the time rules are in PointMap's group panels
  updateNav();
  setTools();
  const view = $('view');
  view.replaceChildren();
  if (['point-map', 'timestamp-rules'].includes(state.view)) state.lastMapping = state.view;

  const isLog = state.view === 'log';
  const readOnly = !FILES[state.view]; // Log / Machines / Outbox: nothing to save
  $('saveBtn').hidden = readOnly;
  $('reassignBtn').hidden = !(state.view === 'point-map' && state.scope); // a machine tab's PointMap only
  $('connectBtn').hidden = state.view !== 'mqtt-config';
  renderTabs();
  renderScopeHint();
  renderProfile();

  if (state.view === 'point-map') renderPointMap(view);
  else if (isLog) renderLog(view);
  else if (state.view === 'machines') renderMachines(view);
  else if (state.view === 'outbox') renderOutbox(view);
  else if (state.view === 'failed') renderFailed(view);
  else renderForm(view);

  $('note').textContent =
    isLog ? 'Newest at the bottom. Full history: logs\\gateway-<date>.log next to the exe (14 days).' :
    state.view === 'failed' ? 'All machines. MES unreachable or slow is not a failure: those rows wait and are retried by themselves (2 s, growing to 60 s).' :
    state.view === 'machines' ? 'Counts since the gateway exe started (a restart from this page keeps them). Updates every 4 s.' :
    state.view === 'outbox' ? 'Read-only. One file per day, kept 7 days after everything in it was delivered. Times shown in this PC\'s local time.' :
    FILES[state.view].restartToApply ? 'Applies after the gateway restarts (shared by all machines).' :
    state.scope ? `Applies as soon as you save. Only ${state.scope} uses this mapping and its time rules. MES fields: the MES format, shared by all machines.` :
    holdPolicy() ? 'Template: "+" copies this into a new machine tab. Machines without a tab are held, not mapped with this.' :
    'Applies as soon as you save. Used by every machine that has no own mapping.';
}

// ---------- Machines: who is connected and how much they sent ----------

function ago(iso) {
  if (!iso) return '—';
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  return s < 60 ? `${s} s ago` : s < 3600 ? `${Math.floor(s / 60)} min ago` : `${Math.floor(s / 3600)} h ${Math.floor(s % 3600 / 60)} min ago`;
}

const localTime = iso => iso ? new Date(iso).toLocaleString() : '—';

function table(headers, rows) {
  return h('div', { class: 'table-wrap' }, h('table', { class: 'data' },
    h('thead', {}, h('tr', {}, ...headers.map(t => h('th', { textContent: t })))),
    h('tbody', {}, ...rows)));
}

// One "issues" cell instead of a column per counter: only the counters that are not 0.
const issues = parts => parts.filter(([n]) => n > 0).map(([n, what]) => `${n.toLocaleString()} ${what}`).join(' · ') || '—';

function renderMachines(view) {
  const data = state.machines;
  if (!data) {
    view.append(h('div', { class: 'empty', textContent: 'Loading…' }));
    return;
  }

  const sum = key => data.machines.reduce((n, m) => n + (m[key] ?? 0), 0);
  const stat = (label, value, small, cls) => h('div', { class: `stat${cls ? ' ' + cls : ''}` }, h('span', { textContent: label }), h('b', {}, value, small ? h('small', { textContent: ` ${small}` }) : null));
  const rate = state.status?.stats?.inPerSec;
  view.append(h('div', { class: 'stats' },
    stat('Connected', String(machineCount().online), `of ${machineCount().all}`),
    stat('Lines in', rate == null ? '—' : rate < 10 ? rate.toFixed(1) : Math.round(rate).toLocaleString(), '/ s'),
    stat('Held (no tab)', String(state.held.length), null, state.held.length ? 'bad' : ''),
    stat('Time warnings', sum('timeWarnings').toLocaleString(), 'last 10 min', sum('timeWarnings') ? 'warn' : '')));

  // First: machines sending without a tab. Their data is held (not processed, not
  // answered) until someone registers them here.
  if (state.held.length) {
    view.append(h('h3', { class: 'sub held-title' }, icon('i-info'), `Not registered: ${state.held.length} machine(s) sending without a tab, data held`));
    view.append(h('p', { class: 'section-note', textContent:
      'Their adapters keep the data. Register a machine: it gets its own tab copied from the mapping you pick, its adapter is reconnected and sends the held data again, and the tags it sent appear on its Unidentified page to map.' }));
    const sources = [['', sharedLabel()], ...state.machineConfigs.filter(c => c.readable).map(c => [c.machineId, c.machineId])];
    view.append(table(
      ['machine', 'connection', 'since', 'held lines', 'data sources', 'tags sent', 'register, copying the mapping of'],
      state.held.map(m => {
        // The choice is kept in state: the view is redrawn every 4 s, which would reset it (#11).
        const picked = state.copyFrom[m.machineId];
        const choice = sources.some(([v]) => v === picked) ? picked : guessCopyFrom(m.machineId);
        const from = select(sources, choice, v => { state.copyFrom[m.machineId] = v; }, 'copy mapping from');
        return h('tr', {},
          h('td', { class: 'strong', textContent: m.machineId }),
          h('td', { textContent: m.connection }),
          h('td', { textContent: ago(m.firstSeen), title: localTime(m.firstSeen) }),
          h('td', { class: 'num warn', textContent: m.lines.toLocaleString() }),
          h('td', { class: 'small', textContent: m.dataSources.join(', ') || '—' }),
          h('td', { class: 'small', textContent: `${m.tags.length}: ${m.tags.slice(0, 8).join(', ')}${m.tags.length > 8 ? ' …' : ''}`, title: m.tags.join(', ') }),
          h('td', { class: 'register-cell' }, from,
            h('button', { type: 'button', class: 'btn primary small', textContent: 'Register', onclick: () => registerMachine(m.machineId, from.value) })));
      })));
    view.append(h('h3', { class: 'sub', textContent: 'All machines' }));
  }

  const machines = data.machines.filter(m => matches(`${m.machineId} ${m.connection} ${m.status} ${m.dataSources.map(d => d.name).join(' ')}`));
  const online = data.machines.filter(m => m.status !== 'disconnected').length;
  const openConnections = data.connections.filter(c => c.open).length;
  view.append(h('p', { class: 'section-note', textContent:
    `${data.machines.length} machine(s) sent data since the gateway started (${online} connected now) · ${openConnections} open connections` }));

  view.append(machines.length ? table(
    ['machine', 'tab', 'connection', 'last message', 'messages', 'to MES / MQTT', 'per s', 'issues', 'data sources'],
    machines.map(m => h('tr', {},
      h('td', { class: 'strong' }, h('span', { class: `dot ${m.status}`, title: m.status }), ` ${m.machineId}`),
      h('td', { class: 'small' }, ownTabLink(m.machineId)),
      h('td', { textContent: m.status === 'disconnected' ? `${m.connection} (gone ${ago(m.disconnectedAt)})` : m.connection }),
      h('td', { textContent: ago(m.lastMessage), title: localTime(m.lastMessage) }),
      h('td', { class: 'num', textContent: m.total.toLocaleString(), title: 'Each message counted once, when it is accepted. Repeats and held tries are not counted; a held message counts when it goes through.' }),
      h('td', { class: 'num', textContent: `${m.mes.toLocaleString()} / ${m.mqtt.toLocaleString()}` }),
      h('td', { class: 'num', textContent: m.perSecond.toFixed(1) }),
      h('td', {
        class: m.held ? 'small bad' : (m.repeats || m.timeWarnings) ? 'small warn' : 'small',
        textContent: issues([[m.held, 'held'], [m.repeats, 'repeats'], [m.timeWarnings, 'time warnings']]),
        title: 'Held: waiting right now. Repeats and time warnings: the last 10 minutes.',
        title: 'held: messages waiting right now (no tab, or tags not mapped), counted as messages once they go through\n' +
               'repeats: message_id already received in the last 10 min, ignored\n' +
               'time warnings: timestamp unreadable (arrival time used) or over an hour from arrival (used as sent); the log says which',
      }),
      h('td', { class: 'small', textContent: m.dataSources.map(d => `${d.name} ${d.count.toLocaleString()}`).join(' · ') || '—' })))
  ) : h('div', { class: 'empty', textContent: data.machines.length ? 'No machine matches the search.' : 'No machine has sent anything yet.' }));

  renderMessageSearch(view, data);

  view.append(h('h3', { class: 'sub', textContent: 'Connections' }));
  view.append(data.connections.length ? table(
    ['connection', 'since', 'lines', 'issues', 'last line', 'machines'],
    data.connections.map(c => h('tr', {},
      h('td', { class: 'strong' }, h('span', { class: `dot ${c.open ? 'connected' : 'disconnected'}`, title: c.open ? 'open' : 'closed' }), ` ${c.endpoint}`),
      h('td', { textContent: c.open ? localTime(c.connectedAt) : `closed ${ago(c.closedAt)}` }),
      h('td', { class: 'num', textContent: c.lines.toLocaleString(), title: 'Every line received on this connection, including repeats and resends of held messages' }),
      h('td', {
        class: c.rejected || c.unreadable ? 'small bad' : c.duplicates ? 'small warn' : 'small',
        textContent: issues([[c.rejected, 'rejected'], [c.unreadable, 'unreadable'], [c.duplicates, 'repeats']]),
        title: 'rejected: no machine_id or message_id (not used)\nunreadable: not valid JSON\nrepeats: message_id already received in the last 10 min, ignored',
      }),
      h('td', { textContent: ago(c.lastLine) }),
      h('td', { class: 'small', textContent: c.machines.join(', ') || '—' })))
  ) : h('div', { class: 'empty', textContent: 'No connection yet.' }));
}

// Message log: every line received (time + machine), kept 7 days. Search a day, from / to time,
// all machines or one: total, per machine, per hour. The inputs and the last result live in
// state, so the Machines view's redraw every 4 s keeps them.
function renderMessageSearch(view, data) {
  const s = state.msgSearch ??= { day: new Date().toLocaleDateString('sv'), from: '00:00', to: '23:59', machine: '', result: null, error: '' };
  const names = [...new Set([...data.machines.map(m => m.machineId), ...state.machineConfigs.map(m => m.machineId)])].sort();
  const field = (type, key, label) => h('input', { type, value: s[key], 'aria-label': label, oninput: e => { s[key] = e.target.value; } });
  view.append(h('h3', { class: 'sub' }, icon('i-pulse'), 'Messages received (kept 7 days)'),
    h('div', { class: 'tools', style: 'margin-bottom:10px' },
      field('date', 'day', 'day'), field('time', 'from', 'from time'), h('span', { class: 'section-note', style: 'margin:0', textContent: 'to' }), field('time', 'to', 'to time'),
      select([['', 'all machines'], ...names.map(n => [n, n])], s.machine, v => { s.machine = v; }, 'machine'),
      h('button', { type: 'button', class: 'btn primary small', textContent: 'Search', onclick: searchMessages })));
  if (s.error) { view.append(h('p', { class: 'note-warn', textContent: s.error })); return; }
  const r = s.result;
  if (!r) return;
  view.append(h('p', { class: 'section-note', textContent:
    `${r.total.toLocaleString()} line(s) on ${s.day}, ${s.from}–${s.to}${s.machine ? ` from ${s.machine}` : ''} · stored days: ${(r.storedBytes / 1048576).toFixed(1)} MB` }));
  if (!r.total) return;
  // Lines per hour of the day, as bars (hover for the number).
  const perHour = new Array(24).fill(0);
  for (const x of r.hours) perHour[Number(x.hour.slice(11, 13))] += x.count;
  const top = Math.max(...perHour, 1);
  view.append(h('div', { class: 'msg-results' },
    table(['machine', 'lines'], r.machines.map(m => h('tr', {}, h('td', { textContent: m.machine }), h('td', { class: 'num', textContent: m.count.toLocaleString() })))),
    h('div', { class: 'card' },
      h('div', { class: 'head' }, icon('i-clock'), 'Lines per hour'),
      h('div', { class: 'hist', role: 'img', 'aria-label': `Lines per hour: ${perHour.map((n, i) => `${i}:00 ${n}`).filter((_, i) => perHour[i]).join(', ')}` },
        ...perHour.map((n, i) => h('i', { class: n ? '' : 'zero', style: `height:${Math.max(2, Math.round(n / top * 100))}%`, title: `${String(i).padStart(2, '0')}:00–${String(i).padStart(2, '0')}:59 · ${n.toLocaleString()} lines` }))),
      h('div', { class: 'hist-x' }, ...perHour.map((_, i) => h('span', { textContent: i % 3 === 0 ? String(i) : '' }))))));
}

async function searchMessages() {
  const s = state.msgSearch;
  const q = new URLSearchParams({ from: `${s.day}T${s.from}`, to: `${s.day}T${s.to}:59.999`, machine: s.machine });
  try { s.result = await request(`/api/messages?${q}`); s.error = ''; }
  catch (e) { s.result = null; s.error = `Not searched: ${e.message}`; }
  render();
}

// "own tab" (opens it) for a machine with its own mapping; "held" when it
// sends without one (and that is held); else "shared".
function ownTabLink(machineId) {
  const own = state.machineConfigs.find(c => c.machineId.toLowerCase() === machineId.toLowerCase());
  if (own) return h('button', { type: 'button', class: 'link', textContent: 'own tab', onclick: () => switchTab(own.machineId) });
  if (state.held.some(m => m.machineId.toLowerCase() === machineId.toLowerCase())) return h('span', { class: 'bad', textContent: 'held: no tab' });
  return holdPolicy() ? 'no tab' : 'shared';
}

// Machines of one kind: the same name before the first "-" (NEXIM-…), or, without a "-",
// the same name without its trailing digits (NXT101 ~ NXT105). The gateway uses the same
// rule (MachineRegistry.Family) for tabs it creates by itself.
// Same rule as MachineRegistry.Family: the name before the first "-", else the name with its numbers left out.
const familyOf = id => (id.includes('-') ? id.slice(0, id.indexOf('-')) : id.replace(/\d+/g, '#').replace(/[#_.]+$/, '')).toLowerCase();

// A machine named like an existing tab most likely is the same model: suggest copying
// that tab (a confirmed one first). Else the template.
function guessCopyFrom(machineId) {
  const family = familyOf(machineId);
  const same = state.machineConfigs.filter(c => c.readable && family && familyOf(c.machineId) === family);
  return (same.find(c => c.autoCopiedFrom == null) ?? same[0])?.machineId ?? '';
}

// Rows of the machine on screen still waiting for MES (all its outbox days), at most every 15 s.
async function loadWaiting() {
  const scope = state.scope || '';
  if (Date.now() - (state.waitingAt[scope] ?? 0) < 15000) return;
  state.waitingAt[scope] = Date.now();
  try { state.waiting[scope] = (await request(`/api/outbox/days?scope=${encodeURIComponent(scope)}`)).reduce((n, d) => n + d.waiting, 0); }
  catch { delete state.waiting[scope]; } // no outbox yet
}

async function pollMachines() {
  try { state.machines = await request('/api/machines'); } catch { return; }
  renderTabs(); // the connection dots in the machine list
  if (isMachineTab(state.tab)) { await loadWaiting(); renderProfile(); } // its status dot, last line, rows waiting
  if (state.view === 'machines') await loadMachineConfigs(); // for the mapping column
  if (state.view !== 'machines') return;
  if (['SELECT', 'INPUT'].includes(document.activeElement?.tagName) && $('view').contains(document.activeElement))
    return; // a dropdown or the message search is in use: redrawing would close it (#11); next poll catches up
  const scroll = [window.scrollY, $('view').scrollTop]; // the refresh must not jump the page back up
  render();
  window.scrollTo(0, scroll[0]);
  $('view').scrollTop = scroll[1];
}

// ---------- Outbox: what went (or waits to go) to MES, per day ----------

async function renderOutbox(view) {
  const o = state.outbox;
  if (!o.days) {
    view.append(h('div', { class: 'empty', textContent: 'Loading…' }));
    if (state.tab === SHARED) {
      // The shared outbox, plus outboxes of machines whose own tab was closed (still delivered).
      const hasTab = s => state.machineConfigs.some(m => m.machineId.toLowerCase() === s.toLowerCase());
      try { o.scopes = (await request('/api/outbox/scopes')).filter(s => !s || !hasTab(s)); } catch { o.scopes = ['']; }
      if (!o.scopes.includes(o.scope)) o.scope = '';
    } else {
      o.scopes = null;
      o.scope = state.scope; // this machine's own outbox
    }
    try { o.days = await request(`/api/outbox/days?scope=${encodeURIComponent(o.scope)}`); } catch (e) { o.days = []; toast(e.message, true); }
    if (!o.days.some(d => d.day === o.day)) o.day = o.days[0]?.day ?? null;
    o.rows = [];
    if (state.view !== 'outbox') return;
    if (o.day) loadOutbox(false); // marks "loading" before the first render
    render();
    return;
  }

  // Other machines tab only: the shared outbox or a closed machine tab's leftover outbox.
  const scopeSelect = o.scopes && o.scopes.length > 1
    ? select(o.scopes.map(s => [s, s ? `${s} (tab closed, outbox still delivered)` : 'Shared outbox']),
        o.scope, v => { o.scope = v; o.days = null; render(); }, 'outbox')
    : null;

  const groups = [...new Set([...pointMapGroups(), 'UNIDENTIFIED'])];
  const knownMachines = (state.machines?.machines ?? []).map(m => m.machineId);
  const reload = () => loadOutbox(false);

  const daySelect = select(o.days.map(d => [d.day, `${d.day}  (${d.rows.toLocaleString()} rows, ${d.waiting.toLocaleString()} waiting${d.failed ? `, ${d.failed.toLocaleString()} rejected` : ''}, ${d.megabytes} MB)`]),
    o.day ?? '', v => { o.day = v; reload(); }, 'day');
  const machineInput = combo(h('input', { value: o.machine, placeholder: 'all machines', 'aria-label': 'machine',
    onchange: e => { o.machine = e.target.value.trim(); reload(); } }),
    text => knownMachines.filter(m => m.toLowerCase().includes(text.toLowerCase())).map(m => ({ value: m })));
  const groupSelect = select([['', 'all groups'], ...groups.map(g => [g, g])], o.group, v => { o.group = v; reload(); }, 'group');
  const sourceInput = h('input', { value: o.source, placeholder: 'all data sources', 'aria-label': 'data source',
    onchange: e => { o.source = e.target.value.trim(); reload(); } });
  const statusSelect = select([['', 'all'], ['sent', 'sent to MES'], ['waiting', 'waiting'], ['failed', 'rejected by MES'], ['discarded', 'discarded']],
    o.status, v => { o.status = v; reload(); }, 'status');

  const day = o.days.find(d => d.day === o.day);
  if (day) {
    const stat = (label, value, cls) => h('div', { class: `stat${cls ? ' ' + cls : ''}` }, h('span', { textContent: label }), h('b', { textContent: value }));
    view.append(h('div', { class: 'stats' },
      stat(`${state.tab === SHARED ? 'Records' : `${state.scope} records`} this day`, day.rows.toLocaleString()),
      stat('Waiting for MES', day.waiting.toLocaleString(), day.waiting ? 'warn' : ''),
      stat('Rejected by MES', (day.failed ?? 0).toLocaleString(), day.failed ? 'bad' : ''),
      stat('Day file', `${day.megabytes} MB`),
      stat('Days kept', o.days.length.toLocaleString())));
  }

  setTools(scopeSelect, daySelect, state.tab === SHARED ? machineInput : null, groupSelect, // a machine's own records are all its own sourceInput, statusSelect,
    h('button', { type: 'button', class: 'btn small', textContent: 'Refresh', onclick: () => { o.days = null; render(); } }));

  if (!o.days.length) {
    view.append(h('div', { class: 'empty', textContent: 'Nothing for MES yet.' }));
    return;
  }

  const rows = o.rows.filter(r => matches(`${r.machineId} ${r.dataSource} ${r.group} ${r.preview}`));
  view.append(rows.length ? table(
    ['', 'created', 'machine', 'data source', 'group', 'MES', 'size', 'payload'],
    rows.flatMap(r => {
      const open = o.open[r.id];
      const tr = h('tr', { class: 'clickable', title: 'Click to show the whole envelope', onclick: () => toggleRow(r.id) },
        h('td', { class: 'disc' }, icon(open ? 'i-down' : 'i-right')),
        h('td', { class: 'nowrap', textContent: new Date(r.createdAt).toLocaleTimeString() }),
        h('td', { class: 'strong', textContent: r.machineId ?? '—' }),
        h('td', { textContent: r.dataSource ?? '—' }),
        h('td', {}, h('span', { class: 'gtag', style: `--c:${gcolor(r.group)}`, textContent: r.group })),
        r.sent ? h('td', { class: 'nowrap' }, h('span', { class: 'pill ok', textContent: `sent ${new Date(r.sentAt).toLocaleTimeString()}` }))
          : r.discardedAt ? h('td', { class: 'nowrap', title: `Discarded ${localTime(r.discardedAt)}: ${r.discardReason}\nMES said: ${r.error ?? ''}` }, h('span', { class: 'pill quiet', textContent: 'discarded' }))
          : r.failedAt ? h('td', { class: 'nowrap', title: `MES said: ${r.error}\nRe-send or discard it under MES failures.` }, h('span', { class: 'pill count', textContent: 'rejected' }))
          : h('td', { class: 'nowrap' }, h('span', { class: 'pill auto', textContent: 'waiting' })),
        h('td', { class: 'num', textContent: r.size >= 1024 ? `${(r.size / 1024).toFixed(1)} KB` : `${r.size} B` }),
        h('td', { class: 'small preview', textContent: r.preview }));
      return open ? [tr, h('tr', {}, h('td', { colSpan: 8 }, h('pre', { class: 'json', textContent: open })))] : [tr];
    })
  ) : h('div', { class: 'empty', textContent: o.loading ? 'Loading…' : 'No rows for these filters.' }));

  if (o.more)
    view.append(h('button', { type: 'button', class: 'btn small', textContent: 'Load older rows', onclick: () => loadOutbox(true) }));
}

// The Outbox view loads again for the tab now shown.
function resetOutbox() {
  const o = state.outbox;
  o.days = null; o.day = null; o.rows = []; o.open = {}; o.more = false;
}

async function loadOutbox(more) {
  const o = state.outbox;
  if (!o.day) return;
  o.loading = true;
  const params = new URLSearchParams({ scope: o.scope, day: o.day, limit: '100' });
  if (o.machine) params.set('machine', o.machine);
  if (o.group) params.set('group', o.group);
  if (o.source) params.set('source', o.source);
  if (o.status) params.set('status', o.status);
  if (more && o.rows.length) params.set('before', o.rows[o.rows.length - 1].id);

  try {
    const page = (await request(`/api/outbox?${params}`)).rows;
    o.rows = more ? o.rows.concat(page) : page;
    o.more = page.length === 100;
    if (!more) o.open = {};
  } catch (e) {
    toast(e.message, true);
  }
  o.loading = false;
  if (state.view === 'outbox') render();
}

async function toggleRow(id) {
  const o = state.outbox;
  if (o.open[id]) { delete o.open[id]; render(); return; }
  try {
    const envelope = await request(`/api/outbox/row?scope=${encodeURIComponent(o.scope)}&day=${encodeURIComponent(o.day)}&id=${id}`);
    o.open[id] = JSON.stringify(envelope, null, 2);
  } catch (e) {
    toast(e.message, true);
  }
  render();
}

// ---------- MES failures: messages MES rejected as invalid ----------

const failedKey = r => `${r.scope}|${r.day}|${r.id}`;

function renderFailed(view) {
  const f = state.failed;
  if (!f) {
    view.append(h('div', { class: 'empty', textContent: 'Loading…' }));
    loadFailed();
    return;
  }

  view.append(h('p', { class: 'section-note', textContent:
    'MES answered these messages with "invalid" (a 4xx), so they are not retried. They stay in the outbox with MES\'s answer. ' +
    'Fix the cause (mapping, MES format, or on the MES side), then re-send. Discard only with a reason: it is logged and kept with the rows.' }));

  const picked = f.rows.filter(r => state.failedPicked.has(failedKey(r)));
  const refs = rows => rows.map(r => ({ scope: r.scope, day: r.day, id: r.id }));
  setTools(
    h('button', { type: 'button', class: 'btn primary small', textContent: picked.length ? `Re-send ${picked.length} ticked` : 'Re-send all',
      disabled: !f.total, onclick: () => failedAction('resend', picked.length ? refs(picked) : null) }),
    h('button', { type: 'button', class: 'btn ghost small', textContent: picked.length ? `Discard ${picked.length} ticked…` : 'Discard all…',
      disabled: !f.total, onclick: () => failedAction('discard', picked.length ? refs(picked) : null) }),
    h('button', { type: 'button', class: 'btn small', textContent: 'Refresh', onclick: () => loadFailed() }));

  if (!f.total) {
    view.append(h('div', { class: 'statusbar' }, h('span', { class: 'dot connected' }), h('b', { textContent: 'MES refused nothing' }),
      h('span', { textContent: 'Records MES answers as invalid wait here to be re-sent or discarded.' })));
    return;
  }

  const rows = f.rows.filter(r => matches(`${r.machineId} ${r.dataSource} ${r.group} ${r.error} ${r.eventId}`));
  const all = h('input', { type: 'checkbox', 'aria-label': 'tick all',
    checked: rows.length > 0 && rows.every(r => state.failedPicked.has(failedKey(r))),
    onchange: e => { rows.forEach(r => e.target.checked ? state.failedPicked.add(failedKey(r)) : state.failedPicked.delete(failedKey(r))); render(); } });

  view.append(h('p', { class: 'section-note', textContent: f.total > f.rows.length
    ? `${f.total.toLocaleString()} rejected, oldest ${f.rows.length} shown.` : `${f.total.toLocaleString()} rejected.` }));
  view.append(h('div', { class: 'table-wrap' }, h('table', { class: 'data' },
    h('thead', {}, h('tr', {}, h('th', {}, all), ...['', 'machine', 'group', 'data source', 'event time', 'rejected', 'MES said', ''].map(t => h('th', { textContent: t })))),
    h('tbody', {}, ...rows.flatMap(r => {
      const key = failedKey(r);
      const open = state.failedOpen[key];
      const tick = h('input', { type: 'checkbox', checked: state.failedPicked.has(key), 'aria-label': 'tick',
        onclick: e => e.stopPropagation(),
        onchange: e => { e.target.checked ? state.failedPicked.add(key) : state.failedPicked.delete(key); render(); } });
      const tr = h('tr', { class: 'clickable', title: 'Click to show the whole envelope', onclick: () => toggleFailed(r) },
        h('td', {}, tick),
        h('td', { class: 'disc' }, icon(open ? 'i-down' : 'i-right')),
        h('td', { class: 'strong', textContent: r.machineId ?? '—' }),
        h('td', {}, h('span', { class: 'gtag', style: `--c:${gcolor(r.group)}`, textContent: r.group })),
        h('td', { textContent: r.dataSource ?? '—' }),
        h('td', { class: 'nowrap', textContent: r.eventTime ? localTime(r.eventTime) : '—' }),
        h('td', { class: 'nowrap', textContent: ago(r.failedAt), title: localTime(r.failedAt) }),
        h('td', { class: 'small bad error-text', textContent: r.error }),
        h('td', { class: 'nowrap' },
          h('button', { type: 'button', class: 'link', textContent: 're-send', onclick: e => { e.stopPropagation(); failedAction('resend', refs([r])); } }),
          ' · ',
          h('button', { type: 'button', class: 'link', textContent: 'discard', onclick: e => { e.stopPropagation(); failedAction('discard', refs([r])); } })));
      return open ? [tr, h('tr', {}, h('td', { colSpan: 9 }, h('pre', { class: 'json', textContent: open })))] : [tr];
    })))));
}

async function loadFailed() {
  try { state.failed = await request('/api/failed?limit=500'); }
  catch (e) { state.failed = { total: 0, rows: [] }; toast(e.message, true); }
  const present = new Set(state.failed.rows.map(failedKey));
  for (const key of [...state.failedPicked]) if (!present.has(key)) state.failedPicked.delete(key);
  state.failedCount = state.failed.total;
  if (state.view === 'failed') {
    const scroll = window.scrollY;
    render();
    window.scrollTo(0, scroll);
  } else renderTabs();
}

async function toggleFailed(r) {
  const key = failedKey(r);
  if (state.failedOpen[key]) { delete state.failedOpen[key]; render(); return; }
  try {
    const envelope = await request(`/api/outbox/row?scope=${encodeURIComponent(r.scope)}&day=${encodeURIComponent(r.day)}&id=${r.id}`);
    state.failedOpen[key] = JSON.stringify(envelope, null, 2);
  } catch (e) {
    toast(e.message, true);
  }
  render();
}

// rows null = all failed messages.
async function failedAction(action, rows) {
  const which = rows ? `${rows.length} message(s)` : `all ${state.failed?.total ?? state.failedCount} rejected message(s)`;
  let reason = null;
  if (action === 'discard') {
    reason = (prompt(`Discard ${which}? They are never sent to MES.\nReason (required, it is logged):`) ?? '').trim();
    if (!reason) { toast('Not discarded: a reason is needed.', true); return; }
  } else if (!rows && !confirm(`Re-send ${which} to MES?`)) {
    return;
  }

  try {
    const result = await request(`/api/failed/${action}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(rows ? { rows, reason } : { all: true, reason }),
    });
    toast(action === 'resend'
      ? `${result.count} message(s) back in the send queue. If MES rejects them again they come back here.`
      : `${result.count} message(s) discarded. Reason logged.`);
  } catch (e) {
    toast(`Not done: ${e.message}`, true);
  }
  state.failedPicked.clear();
  await loadFailed();
}

// Log: everything the gateway printed, problems in red.
function renderLog(view) {
  state.seenErrors = state.errorCount;
  state.seenLogId = state.logs.at(-1)?.id ?? state.seenLogId;
  updateErrorBadge();

  // Problems = the ones still going on: one that is over (MES / MQTT came back) clears by itself.
  // All keeps every line, a problem that is over marked "solved".
  const solved = solvedProblems(state.logs);
  const problems = state.logs.filter(l => l.isError && !solved.has(l.id)).length;
  setTools(seg([[false, 'All'], [true, `Problems (${problems})`]], state.errorsOnly, v => { state.errorsOnly = v; render(); }, 'Show'));

  const lines = state.logs.filter(l => (!state.errorsOnly || (l.isError && !solved.has(l.id))) && matches(l.text));
  const box = h('div', { class: 'log', id: 'logBox' });
  if (!lines.length)
    box.append(h('div', { class: 'empty', style: 'padding:12px', textContent: state.errorsOnly && !problems ? 'No problems going on right now.' : state.logs.length ? 'No lines match.' : 'No log lines yet.' }));
  for (const line of lines) {
    // "[config] text": the part in brackets gets its own column, coloured by who wrote it.
    const m = line.text.match(/^\[([^\]]+)\]\s*(.*)$/s);
    const who = m ? m[1] : '';
    const done = line.isError && solved.has(line.id), back = !line.isError && RECOVERED.test(line.text);
    box.append(h('div', { class: `log-line${line.isError ? ' err' : ''}${done ? ' solved' : ''}${back ? ' back' : ''}` },
      h('span', { class: 't', textContent: new Date(line.time).toLocaleTimeString() }),
      h('span', { class: 'm', style: line.isError ? '' : `color:${LOG_COLORS[who.split(' ')[0]] ?? 'var(--muted)'}`, textContent: who }),
      h('span', { class: 'x' }, done ? h('span', { class: 'pill ok', textContent: 'solved' }) : null, done ? ' ' : null, m ? m[2] : line.text)));
  }

  view.append(box);
  box.scrollTop = box.scrollHeight;
}

const LOG_COLORS = {
  tcp: 'var(--g-TRACEABILITY)', gateway: 'var(--ok)', mqtt: 'var(--accent)', twin: 'var(--accent)',
  config: 'var(--gold)', mapping: 'var(--g-PROCESS)', forwarder: 'var(--g-MATERIAL)', outbox: 'var(--g-MATERIAL)', alarm: 'var(--bad)',
};

// Ongoing problems, read from the gateway's own log lines. These are fixed texts in
// MesForwarder.cs and MqttPublisher*.cs / TwinForwarder.cs: change them together.
function trackProblem(line) {
  const text = line.text;
  if (/^\[gateway\] running:/.test(text)) {
    // A new run (start or restart) has new MES senders and MQTT clients: they log again if still failing.
    state.problems.mes.clear();
    return;
  }
  const forwarder = text.match(/^\[forwarder(?: ([^\]]+))?\]/);
  if (forwarder) {
    const who = forwarder[1] ?? '';
    if (/ send failed for | MES returned \d+ /.test(text)) { if (!state.problems.mes.has(who)) state.problems.mes.set(who, line.time); }
    else if (/ MES reachable again /.test(text)) state.problems.mes.delete(who);
    return;
  }
}

function renderAlerts() {
  const items = [];
  const status = state.status;
  const since = time => new Date(time).toLocaleTimeString();
  if (state.unreachable)
    items.push(['bad', 'This page cannot reach the gateway. Is it still running?']);
  else if (status?.state === 'error')
    items.push(['bad', `Gateway stopped: ${status.lastError}. It tries again by itself.`]);

  if (state.problems.mes.size) {
    const first = [...state.problems.mes.values()].sort()[0];
    const who = [...state.problems.mes.keys()].filter(Boolean);
    const backlog = status?.stats?.backlog;
    items.push(['warn', `MES not reachable since ${since(first)}${who.length ? ` (${who.join(', ')})` : ''}. ` +
      `${backlog > 0 ? `${backlog.toLocaleString()} records wait` : 'Records wait'} in the outbox and are sent when MES is back.`]);
  }
  if (state.problems.mqtt)
    items.push(['warn', `MQTT broker not reachable since ${since(state.problems.mqtt)}: IO / motion data and the keepalive are not published (they are not kept).`]);

  $('alerts').replaceChildren(...items.map(([kind, text]) => h('div', { class: `alert ${kind}` }, text,
    h('button', { type: 'button', class: 'link', textContent: 'view log', onclick: () => switchView('log') }))));
  $('alerts').hidden = !items.length;
}

function updateErrorBadge() {
  // New problems still going on: one that is over (MES / MQTT came back) clears by itself.
  // Problems no longer in the page's lines can't be checked: they count as going on.
  const solved = solvedProblems(state.logs);
  const over = state.logs.filter(l => l.isError && l.id > state.seenLogId && solved.has(l.id)).length;
  const open = state.errorCount - state.seenErrors - over;
  $('errBadge').hidden = open <= 0;
  $('errBadge').replaceChildren(icon('i-info'), `${open} new problem${open === 1 ? '' : 's'}`);
}

// A problem that came back on its own: a later "MES reachable again" from the same forwarder, or MQTT
// connected again after an [mqtt] / [twin] problem. Fixed texts in MesForwarder.cs / MqttPublisher*.cs.
const RECOVERED = /^\[forwarder[^\]]*\] MES reachable again |^\[mqtt\] connected to /;
function problemKind(text) {
  const who = text.match(/^\[([^\]]+)\]/)?.[1] ?? '';
  return who.startsWith('forwarder') ? who : /^(mqtt|twin)\b/.test(who) ? 'mqtt' : null;
}
// Ids of the problems that are over, and of the lines that ended them.
function solvedProblems(lines) {
  const solved = new Set(), open = new Map(); // kind -> its problem ids since it last came back
  for (const line of lines) {
    const kind = problemKind(line.text);
    if (!kind) continue;
    if (RECOVERED.test(line.text)) {
      if (open.has(kind)) [...open.get(kind), line.id].forEach(id => solved.add(id));
      open.delete(kind);
    } else if (line.isError) {
      if (!open.has(kind)) open.set(kind, []);
      open.get(kind).push(line.id);
    }
  }
  return solved;
}

async function pollLogs() {
  let result;
  try { result = await request(`/api/logs?after=${state.lastLogId}`); }
  catch { return; }

  const fresh = result.lines;
  if (fresh.length) {
    state.logs.push(...fresh);
    if (state.logs.length > MAX_LOG_LINES) state.logs.splice(0, state.logs.length - MAX_LOG_LINES);
    state.lastLogId = fresh[fresh.length - 1].id;
  }
  state.errorCount = result.errorCount;

  // No pop-up per problem line (they repeat every minute while MES or the broker is
  // down): ongoing problems are one banner each at the top, the badge counts the lines.
  fresh.forEach(trackProblem);
  renderAlerts();

  if (state.view === 'log') {
    if (fresh.length) {
      const box = $('logBox');
      const atBottom = !box || box.scrollHeight - box.scrollTop - box.clientHeight < 40;
      const keepTop = box?.scrollTop ?? 0;
      render(); // scrolls to the newest line
      if (!atBottom) $('logBox').scrollTop = keepTop; // reading older lines: stay there
    }
  } else {
    updateErrorBadge();
  }
}

// PointMap: the loadout row (one slot per MES group: how many of its fields are filled), then one
// panel per group: MES fields (type the incoming tag) and extra fields; non-MES groups (MOTION, IO) free rows.
function renderPointMap(view) {
  const groups = pointMapGroups();
  const unid = unidentifiedList();
  if (state.group['point-map'] === UNID) {
    renderUnidentified(view, unid, groups);
    return;
  }

  const filter = state.pmFilter ??= 'all';
  setTools(
    seg([['all', 'All fields'], ['filled', 'Filled'], ['empty', 'Empty'], ['extras', 'Extras']], filter, v => { state.pmFilter = v; render(); }, 'Show'),
    h('button', { type: 'button', class: 'btn ghost small', title: 'A data group of your own (not an MES table): its rows go to MQTT or nowhere', onclick: addNewGroup }, icon('i-plus'), 'New group'));

  view.append(loadoutRow());

  const panels = h('div', { class: 'groups' });
  let shown = 0;
  for (const g of groups.filter(g => !state.onlyGroup || g === state.onlyGroup)) shown += renderGroupFields(panels, g);
  view.append(panels);
  if (!shown)
    view.append(h('div', { class: 'empty', textContent: state.search.trim() ? 'Nothing matches the search.' : 'Nothing to show with this filter.' }));

  // A slot or a twin chip asked for a group: bring its panel into view.
  if (state.focusGroup) {
    const panel = $(`grp-${state.focusGroup}`);
    state.focusGroup = null;
    if (panel) {
      panel.scrollIntoView({ block: 'start', behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
      panel.classList.add('flash');
    }
  }
}

const loadoutRow = () => h('div', { class: 'loadout', role: 'list', 'aria-label': 'MES groups: fields filled' },
  ...pointMapGroups().filter(g => mesColumns(g)).map(loadoutSlot));

// After a tag edit: redraw that group's panel (and PRODUCTION's: its cycle_time follows other groups),
// the slots and the machine card, then put the focus back where the user moved it (Tab, a click).
function refreshGroup(group) {
  setTimeout(() => {
    const focused = document.activeElement;
    let again = null;
    for (const g of new Set([group, 'PRODUCTION'])) {
      const old = $(`grp-${g}`);
      if (!old) continue;
      const label = old.contains(focused) ? focused.getAttribute('aria-label') : null;
      const fresh = h('div', {});
      renderGroupFields(fresh, g);
      old.replaceWith(...fresh.childNodes);
      if (label) again = () => $(`grp-${g}`)?.querySelector(`[aria-label="${CSS.escape(label)}"]`)?.focus();
    }
    document.querySelector('.loadout')?.replaceWith(loadoutRow());
    renderProfile();
    again?.();
  });
}

// The MES columns of a data group (the MES format, Config/mes-schema.json), or null for a group that is no MES table (MOTION, IO, ...).
const mesColumns = group => state.mesSchema?.[group] ?? null;

// Fields the gateway fills by itself, so they need no tag: a Span rule's start / end / previous
// value (only when its trigger tag is mapped: without it the span never moves), a single-point
// rule's time field, and PRODUCTION cycle_time when the gateway can work it out (start and end
// in one message, PRODUCTION's or MACHINE_STATUS's, or cycle_active on / off).
function gatewayFilled(group) {
  const rules = state.data['timestamp-rules'] ?? {};
  const rows = state.data['point-map'] ?? [];
  const tagged = g => new Set(rows.filter(r => r.dataGroup === g && (r.tag ?? '').trim()).map(r => r.fieldName));
  const mine = tagged(group);
  const rule = rules[group];
  const out = new Set();
  if (!rule) out.add('record_timestamp');
  else if (rule.mode === 'Span') {
    if (rule.triggerField && mine.has(rule.triggerField))
      [rule.startField, rule.endField, rule.previousField || `previous_${rule.triggerField}`].filter(Boolean).forEach(f => out.add(f));
  } else out.add(rule.timestampField || 'record_timestamp');

  if (group === 'PRODUCTION') {
    const both = g => rules[g]?.startField && rules[g]?.endField && tagged(g).has(rules[g].startField) && tagged(g).has(rules[g].endField);
    if (both('PRODUCTION') || both('MACHINE_STATUS') || (rules.PRODUCTION?.triggerField && mine.has(rules.PRODUCTION.triggerField)))
      out.add('cycle_time');
  }
  return out;
}

// A group's MES fields: filled by a tag, filled by the gateway (and no tag), all.
function groupFill(group) {
  const columns = mesColumns(group) ?? [];
  const rows = state.data['point-map'] ?? [];
  const tagged = new Set(rows.filter(r => r.dataGroup === group && (r.tag ?? '').trim()).map(r => r.fieldName));
  const byGateway = gatewayFilled(group);
  const tags = columns.filter(c => tagged.has(c));
  const gw = columns.filter(c => !tagged.has(c) && byGateway.has(c));
  return { tags: tags.length, gw: gw.length, total: columns.length, gwFields: gw, empty: columns.filter(c => !tagged.has(c) && !byGateway.has(c)) };
}

function loadoutSlot(group) {
  const only = state.onlyGroup;
  const f = groupFill(group);
  const filled = f.tags + f.gw;
  const pct = n => (f.total ? `${(n / f.total) * 100}%` : '0');
  const tip = [`${group}: ${filled} of ${f.total} MES fields filled`,
    f.gw ? `${f.gw} set by the gateway: ${f.gwFields.join(', ')}` : '',
    f.empty.length ? `no tag (sent as null): ${f.empty.join(', ')}` : ''].filter(Boolean).join('\n');
  return h('button', {
    type: 'button', role: 'listitem', class: `slot${filled ? '' : ' empty'}${only === group ? ' sel' : only ? ' off' : ''}`,
    style: `--c:${gcolor(group)}`, title: `${tip}\n${only === group ? 'Click again to show every group.' : 'Click to show only this group.'}`, 'aria-label': tip,
    'aria-pressed': String(only === group),
    onclick: () => { state.onlyGroup = only === group ? null : group; render(); },
  },
    h('span', { class: 'lv', textContent: `${filled}/${f.total}` }),
    // Every MES group has a time rule; a Span one (times from a trigger) is worth a mark on its slot.
    state.data['timestamp-rules']?.[group]?.mode === 'Span' ? h('span', { class: 'clock', title: `Span on ${state.data['timestamp-rules'][group].triggerField}` }, icon('i-clock')) : null,
    h('span', { class: 'tile' }, icon(`g-${gkey(group)}`)),
    h('span', { class: 'nm', textContent: SHORT_GROUP[group] ?? group }),
    h('span', { class: 'bar' }, h('i', { style: `width:${pct(f.tags)}` }), h('i', { class: 'gw', style: `width:${pct(f.gw)}` })));
}

// The group's time rule (how its records get their times, how often they go to MES), folded to one
// line at the top of its PointMap panel; Edit opens it there. A change redraws the panel and the slots
// (fields set by the rule follow).
function ruleStrip(group) {
  const rules = state.data['timestamp-rules'];
  const rule = rules[group];
  if (!rule)
    return h('div', { class: 'rule none' }, h('div', { class: 'rule-sum' }, icon('i-clock'),
      h('span', { class: 'rule-text' }, 'No time rule: each record gets ', h('b', { textContent: 'record_timestamp' })),
      h('button', { type: 'button', class: 'link', textContent: 'Add a time rule', onclick: () => addTimestampRule(group) })));

  rule.mode ??= 'SinglePoint';
  const span = rule.mode === 'Span';
  const open = state.ruleOpen.has(group);
  const sum = h('button', {
    type: 'button', class: 'rule-sum', 'aria-expanded': String(open),
    onclick: () => { if (open) state.ruleOpen.delete(group); else state.ruleOpen.add(group); refreshGroup(group); },
  }, icon('i-clock'), h('span', { class: 'rule-kind', textContent: span ? 'Span' : 'Single point' }),
    h('span', { class: 'rule-text' }, ...(span ? ['on ', h('b', { textContent: rule.triggerField || '?' })] : ['time → ', h('b', { textContent: rule.timestampField || 'record_timestamp' })]),
      ` · sent every ${fmtMs(rule.sendEveryMs ?? 1000)}`),
    h('span', { class: 'rule-edit' }, open ? 'Close' : 'Edit', icon('i-down')));
  if (!open) return h('div', { class: 'rule' }, sum);

  const changed = () => { markDirty('timestamp-rules'); refreshGroup(group); };
  const kv = (label, input) => h('div', { class: 'kv' }, h('span', { textContent: label }), input);
  const text = (key, placeholder) => h('input', {
    value: rule[key] ?? '', placeholder: placeholder ?? '', 'aria-label': `${group} ${key}`,
    oninput: e => { rule[key] = e.target.value; markDirty('timestamp-rules'); },
    onchange: changed, // leaving the box: the summary and the fields set by the rule follow
  });
  const note = !span ? 'Every change gets the machine\'s time (the arrival time when it sent none).'
    : group === 'ALARM' ? `When ${rule.triggerField || 'the trigger'} goes on, the alarm starts; when it goes off, the record gets its start and clear time.`
    : `When ${rule.triggerField || 'the trigger'} changes, the running span ends and a new one starts.${group === 'PRODUCTION' ? ' cycle_time = how long it was on.' : ''}`;
  return h('div', { class: 'rule open' }, sum, h('div', { class: 'rule-body' },
    kv('Mode', select([['SinglePoint', 'Single point: the time of each change'], ['Span', 'Span: start / end of each value']], rule.mode,
      v => { rule.mode = v; changed(); }, `${group} mode`)),
    ...(span
      ? [kv('Span changes when', text('triggerField')), kv('Start time goes to', text('startField')), kv('End time goes to', text('endField')),
         kv('Previous value goes to', text('previousField', `previous_${rule.triggerField || '<trigger>'} (default)`))]
      : [kv('Time goes to', text('timestampField', 'record_timestamp'))]),
    kv('Send to MES at most every (ms)', h('input', {
      type: 'number', min: '0', step: '1', value: rule.sendEveryMs ?? '', placeholder: '1000 (default)', 'aria-label': `${group} sendEveryMs`,
      oninput: e => {
        if (e.target.value === '') delete rule.sendEveryMs;
        else rule.sendEveryMs = Number(e.target.value);
        markDirty('timestamp-rules');
      },
      onchange: changed,
    })),
    h('div', { class: 'grp-note', textContent: `${note} Times the machine sends itself win.` }),
    h('div', { class: 'grp-foot' }, h('button', {
      type: 'button', class: 'link', textContent: 'Remove time rule',
      onclick: () => {
        if (!confirm(`Remove the time rule of ${group}? Its records then get record_timestamp.`)) return;
        delete rules[group];
        state.ruleOpen.delete(group);
        changed();
      },
    }))));
}

// One group's panel. Returns how many rows it shows (0 = left out by the search or filter).
function renderGroupFields(container, group) {
  const columns = mesColumns(group);
  const filter = state.pmFilter ?? 'all';
  const searching = state.search.trim() !== '';
  const twin = TWIN_GROUPS.includes(group);
  const panel = h('article', { class: 'card grp', id: `grp-${group}`, style: `--c:${gcolor(group)}` });
  const fill = columns ? groupFill(group) : null;
  panel.append(h('div', { class: 'head' }, icon(`g-${gkey(group)}`), h('span', { class: 'gname', textContent: group }),
    fill ? h('span', { class: 'fill', textContent: `${fill.tags + fill.gw} / ${fill.total}` }) : null,
    twin ? h('span', { class: 'badge quiet', textContent: 'MQTT · live only' }) : null));
  if (!twin && (columns || state.data['timestamp-rules'][group])) panel.append(ruleStrip(group));
  const rule = state.data['timestamp-rules'][group];
  const ruleFields = !rule ? [] : rule.mode === 'Span'
    ? [rule.startField, rule.endField, rule.previousField || `previous_${rule.triggerField}`] : [rule.timestampField || 'record_timestamp'];
  let shown = 0;

  if (columns && filter !== 'extras') {
    const byGateway = gatewayFilled(group);
    for (const column of columns) {
      const rows = state.data['point-map'].filter(r => r.dataGroup === group && r.fieldName === column);
      const tags = rows.map(r => r.tag).filter(t => (t ?? '').trim());
      const gw = !tags.length && byGateway.has(column);
      if (filter === 'filled' && !tags.length && !gw) continue;
      if (filter === 'empty' && (tags.length || gw)) continue;
      if (!matches(`${column} ${tags.join(' ')} ${group}`)) continue;
      shown++;
      // Mapped automatically by name matching and not checked yet: clicking the mark confirms it (save to keep).
      const auto = rows.filter(r => r.auto != null);
      // "a+b" = joined (both needed, values sent as a_b); "a, b" = either tag fills the field.
      const joined = tags.find(t => t.includes('+'))?.split('+').map(p => p.trim()).filter(Boolean) ?? [];
      panel.append(h('div', { class: `frow${tags.length ? '' : gw ? ' gwfill' : ' none'}${joined.length > 1 ? ' flag' : ''}` },
        h('i', { class: 'mark' }),
        h('span', { class: 'fn', title: `MES column ${column}` }, column,
          auto.length ? h('button', {
            type: 'button', class: 'auto-mark', textContent: `auto ${Math.max(...auto.map(r => r.auto)).toFixed(2)}`,
            title: `Mapped automatically (${auto.map(r => `${r.tag}: ${r.auto}`).join(', ')}). Click to confirm it, then save.`,
            onclick: () => { auto.forEach(r => delete r.auto); markDirty('point-map'); render(); },
          }) : null,
          gw ? h('span', { class: 'note-gw' }, ...(ruleFields.includes(column) ? [icon('i-clock'), 'set by the time rule'] : ['set by the gateway'])) : null,
          rule?.mode === 'Span' && column === rule.triggerField ? h('span', { class: 'trig', textContent: 'starts / ends the span' }) : null),
        h('textarea', {
          rows: 1, value: tags.join(', '), 'aria-label': `incoming tag for ${column}`, title: tags.join(', '),
          onkeydown: e => { if (e.key === 'Enter') { e.preventDefault(); e.target.blur(); } },
          placeholder: gw ? 'optional' : 'no tag · sent as null',
          // on change (leaving the box), so typing a comma doesn't redraw the list
          onchange: e => { setFieldTags(group, column, e.target.value); refreshGroup(group); },
        }),
        tags.length ? h('button', {
          class: 'row-x', type: 'button', 'aria-label': `Take the tag off ${column}`,
          title: `Take ${tags.join(', ')} off ${column} on this tab: it becomes an extra field (not sent). ${column} itself stays.`,
          onclick: () => clearFieldTags(group, column, columns),
        }, icon('i-close')) : h('span')));
      if (joined.length > 1)
        panel.append(h('div', { class: 'flagnote', textContent: `${joined.join(' + ')}: MES gets their values joined with _, and only when all ${joined.length} arrive.` }));
    }
    if (!searching && filter === 'all')
      panel.append(h('div', { class: 'grp-foot' },
        h('button', { type: 'button', class: 'link', textContent: '+ MES field', title: `Add a column to the MES format of ${group} (all machines)`, onclick: () => addMesField(group) })));
  }

  // Extra fields: no MES column (never in the MES payload); "assign it" puts the tag on a MES field.
  // A non-MES group (MOTION, IO): every field, free text.
  if (filter === 'all' || filter === 'extras' || !columns) {
    const extraRows = state.data['point-map'].filter(r => r.dataGroup === group && !(columns ?? []).includes(r.fieldName)
      && matches(`${r.tag} ${r.fieldName} ${r.dataGroup}`));
    if (extraRows.length || !searching) {
      const list = h('div', {});
      for (const row of extraRows) {
        if (columns) appendExtraRow(list, row, group, columns);
        else appendPointRow(list, row, false, group);
      }
      if (!searching) {
        if (columns) appendExtraRow(list, null, group, columns);
        else for (let i = 0; i < BLANK_ROWS; i++) appendPointRow(list, null, false, group);
      }
      panel.append(
        columns ? h('div', { class: 'sub-h', title: 'Extra fields for this tab only: NOT sent to MES. "assign it" puts a tag on a MES field; a field that already has a tag gets both, joined with + (values sent joined with _).' },
          'Extras · not sent to MES', h('span', { class: 'pill quiet', textContent: String(extraRows.length) }))
          : h('div', { class: 'frow extra', 'aria-hidden': 'true' }, h('span', { class: 'fill', textContent: 'incoming tag' }), h('span', { class: 'fill', textContent: 'fieldName' }), h('span')),
        list);
      shown += extraRows.length;
    }
  }

  if (columns && !shown && !searching && filter === 'all') shown = 1; // an empty MES group still shows (to fill it)
  if (shown || (!searching && filter === 'all')) container.append(panel);
  return shown;
}

// The tags typed for one MES field (comma-separated) replace its rows. In place:
// the rows on screen keep the same list, so their edits and × still count (#10).
function setFieldTags(group, column, text) {
  const tags = [...new Set(text.split(',').map(t => t.trim()).filter(Boolean))];
  const rows = state.data['point-map'];
  for (let i = rows.length - 1; i >= 0; i--)
    if (rows[i].dataGroup === group && rows[i].fieldName === column) rows.splice(i, 1);
  for (const tag of tags) rows.push({ tag, dataGroup: group, fieldName: column });
  markDirty('point-map');
}

// Takes one row out of the point map. A row that is not in it (any more) removes
// nothing: splice(-1, 1) would delete the last row of an unrelated group (#10).
function removeRow(row) {
  const rows = state.data['point-map'];
  const i = rows.indexOf(row);
  if (i >= 0) rows.splice(i, 1);
}

// The MES format is shared by every machine: changing it is saved with PointMap.
function addMesField(group) {
  const name = (prompt(`New MES column for ${group} (as named in the MES database). Every machine sends it from now on (null when no tag):`) ?? '').trim();
  if (!name) return;
  if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(name)) { toast('A MES column name uses letters, digits and _ only.', true); return; }
  if (state.mesSchema[group].some(c => c.toLowerCase() === name.toLowerCase())) { toast(`${group} already has ${name}.`, true); return; }
  state.mesSchema[group].push(name);
  state.mesDirty = true;
  markDirty('point-map');
  render();
}

// × on a MES field: its tag(s) go back to extra fields, named after the tag (not sent),
// ready to "assign it" again. The MES field stays: it is part of the MES format, shared by
// every machine, and the page never removes one (edit Config/mes-schema.json for that).
function clearFieldTags(group, column, columns) {
  for (const row of state.data['point-map'].filter(r => r.dataGroup === group && r.fieldName === column)) {
    let name = row.tag.toLowerCase().replace(/[^a-z0-9_]+/g, '_');
    if (columns.includes(name)) name += '_extra'; // named like a MES field it would still go there
    row.fieldName = name;
    delete row.auto;
  }
  markDirty('point-map');
  render();
}

// An extra row of a MES group: incoming tag + "assign it" (pick a MES field).
// Until assigned, the tag stays an extra field (named after the tag, not sent).
// row = null draws a blank row; typing in it adds a real row to the group.
function appendExtraRow(grid, row, group, columns) {
  const rows = state.data['point-map'];
  const filled = gatewayFilled(group);

  const tag = h('input', {
    value: row?.tag ?? '', 'aria-label': 'incoming tag', placeholder: row ? '' : 'new extra tag',
    oninput: e => {
      if (!row) {
        row = { tag: '', dataGroup: group, fieldName: '' };
        rows.push(row);
        appendExtraRow(grid, null, group, columns); // keep a blank row available
      }
      const named = !row.fieldName || row.fieldName === row.tag.trim().toLowerCase();
      row.tag = e.target.value;
      if (named) row.fieldName = row.tag.trim().toLowerCase();
      markDirty('point-map');
    },
  });

  const options = columns.map(column => {
    const has = rows.filter(r => r !== row && r.dataGroup === group && r.fieldName === column && (r.tag ?? '').trim()).map(r => r.tag);
    return [column, has.length ? `${column}  (has ${has.join(', ')}: combine with +)`
      : filled.has(column) ? `${column}  (set by the gateway)` : column];
  });
  const assign = select([['', row?.fieldName ? `not assigned: ${row.fieldName} (not sent)` : 'assign it…'], ...options], '', column => {
    if (!column) return;
    if (!row || !(row.tag ?? '').trim()) {
      toast('Type the incoming tag first.', true);
      assign.value = '';
      return;
    }
    assignExtra(group, row, column);
  }, 'assign it to a MES field');
  assign.title = assign.options[0].text; // the whole "not assigned: …" text, which the box cuts off

  const remove = h('button', {
    class: 'row-x', type: 'button', title: 'Remove row', 'aria-label': 'Remove row',
    onclick: () => {
      if (!row) { tag.value = ''; return; }
      removeRow(row);
      markDirty('point-map');
      render();
    },
  }, icon('i-close'));

  grid.append(h('div', { class: 'frow extra' }, tag, assign, remove));
}

// "assign it": the extra tag goes onto a MES field. A field that already has a
// tag gets both, combined with + (the gateway joins the values with _), so the
// tag already there keeps working. With several tags on the field, the first one
// is combined.
function assignExtra(group, row, column) {
  const rows = state.data['point-map'];
  const tag = row.tag.trim();
  const existing = rows.find(r => r !== row && r.dataGroup === group && r.fieldName === column && (r.tag ?? '').trim());
  if (existing) {
    const parts = existing.tag.split('+').map(t => t.trim()).filter(Boolean);
    if (!parts.some(p => p.toLowerCase() === tag.toLowerCase())) parts.push(tag);
    existing.tag = parts.join('+');
    removeRow(row);
    toast(`${column} now gets ${existing.tag} (values joined with _). Save to apply.`);
  } else {
    row.fieldName = column;
    toast(`${tag} now goes to ${column}. Save to apply.`);
  }
  markDirty('point-map');
  render();
}

// row = null draws a blank row; typing in it adds a real row to the group.
function appendPointRow(grid, row, showGroup, group) {
  const rows = state.data['point-map'];
  const ensureRow = () => {
    if (!row) {
      row = { tag: '', dataGroup: group, fieldName: '' };
      rows.push(row);
      appendPointRow(grid, null, false, group); // keep a blank row available
    }
    return row;
  };

  if (showGroup)
    grid.append(h('div', { class: 'group-tag', textContent: row.dataGroup, title: row.dataGroup }));

  const tag = h('input', {
    value: row?.tag ?? '', 'aria-label': 'incoming tag',
    oninput: e => { ensureRow().tag = e.target.value; markDirty('point-map'); },
  });
  const field = h('input', {
    value: row?.fieldName ?? '', 'aria-label': 'fieldName',
    oninput: e => { ensureRow().fieldName = e.target.value; markDirty('point-map'); },
  });
  const remove = h('button', {
    class: 'row-x', type: 'button', title: 'Remove row', 'aria-label': 'Remove row',
    onclick: () => {
      if (!row) { tag.value = ''; field.value = ''; return; }
      removeRow(row);
      markDirty('point-map');
      render();
    },
  }, icon('i-close'));

  grid.append(h('div', { class: 'frow extra' }, tag, field, remove));
}

// Tags the gateway received that are not in the point map yet.
function renderUnidentified(view, unid, groups) {
  view.append(h('p', {
    class: 'section-note',
    textContent: 'Tags the gateway received that are not in the point map (since it last started). Pick a group and a field (or use the best match), then save to add them. ' +
      'A MES field that already has a tag gets both, joined with + (values sent joined with _).',
  }));

  const list = unid.filter(u => matches(`${u.tag} ${u.lastValue}`));
  if (!list.length) {
    view.append(unid.length
      ? h('div', { class: 'empty', textContent: 'No tags match the search.' })
      : h('div', { class: 'statusbar' }, h('span', { class: 'dot connected' }), h('b', { textContent: 'Nothing unidentified' }),
          h('span', { textContent: 'Every tag received so far is mapped or set to ignore.' })));
    return;
  }

  const rows = list.map(u => {
    const edit = state.unidEdits[u.tag] ??= { dataGroup: '', fieldName: '' };
    const field = combo(h('input', {
      value: edit.fieldName, placeholder: u.tag.toLowerCase(), 'aria-label': `field for ${u.tag}`,
      oninput: e => { edit.fieldName = e.target.value; markDirty('point-map'); },
    }), text => fieldOptions(edit.dataGroup, text), () => edit.dataGroup && edit.dataGroup !== IGNORED ? gcolor(edit.dataGroup) : '');
    // Best MES column by name (gateway's NameMatcher); "use" fills group + field, save confirms.
    const s = u.suggestion;
    const score = s && !s.seenOn ? h('span', { class: 'score' }, h('i', {}, h('b', { style: `width:${Math.round(s.score * 100)}%` })), s.score.toFixed(2)) : null;
    return h('tr', {},
      h('td', { class: 'strong', textContent: u.tag }),
      h('td', { class: 'small', textContent: `${u.lastValue}`, title: `×${u.count} · last seen ${new Date(u.lastSeen).toLocaleString()}` }),
      h('td', {}, select([['', '— pick —'], [IGNORED, 'ignore (never sent)'], ...groups.filter(g => g !== IGNORED && g !== 'UNIDENTIFIED').map(g => [g, g])], edit.dataGroup,
        v => { edit.dataGroup = v; markDirty('point-map'); }, `group for ${u.tag}`)),
      h('td', {}, field),
      h('td', {}, s ? h('span', { class: 'gtag', style: `--c:${gcolor(s.group)}`, textContent: `${s.group} / ${s.field}` }) : h('span', { class: 'small', textContent: '—' })),
      h('td', { class: 'nowrap' }, s?.seenOn ? h('span', { class: 'pill quiet', textContent: `seen on ${s.seenOn} tab${s.seenOn === 1 ? '' : 's'}` }) : score),
      h('td', { class: s?.conflict ? 'note-warn' : 'small', textContent: s ? (s.conflict ?? 'below the auto-map score') : '' }),
      h('td', {}, s ? h('button', { type: 'button', class: 'btn ghost small', textContent: 'Use', onclick: () => {
        edit.dataGroup = s.group; edit.fieldName = s.field; markDirty('point-map'); render();
      } }) : null));
  });

  view.append(table(['incoming tag', 'last value', 'group', 'field (MES column or extra)', 'best match', 'score', 'why not automatic', ''], rows));
}

// The field list for an Unidentified tag: the group's MES fields (for MOTION, IO or a group of your own,
// the fields it already has on this tab), each with what fills it now, then what is typed as a new field.
function fieldOptions(group, text) {
  if (!group) return [{ note: 'Pick a group first' }];
  if (group === IGNORED) return [];
  const rows = state.data['point-map'] ?? [];
  const columns = mesColumns(group);
  const names = columns ?? [...new Set(rows.filter(r => r.dataGroup === group && r.fieldName).map(r => r.fieldName))];
  const byGateway = columns ? gatewayFilled(group) : new Set();
  const t = text.toLowerCase();
  const list = names.filter(n => n.toLowerCase().includes(t)).map(n => {
    const has = rows.filter(r => r.dataGroup === group && r.fieldName === n && (r.tag ?? '').trim()).map(r => r.tag);
    return { value: n, hint: has.length ? `has ${has.join(', ')}` : byGateway.has(n) ? 'set by the gateway' : '' };
  });
  if (text && !names.some(n => n.toLowerCase() === t))
    list.push({ sep: columns ? 'extra field · not sent to MES' : 'new field' }, { value: text, text: `+ use "${text}"`, extra: true });
  return list;
}

// MQTTConfig / GatewayConfig settings in plain words. The file's own name stays
// visible under each label: USE_CASE.md and the log use those names.
// choices: the only values the gateway accepts (a drop-down, not free text).
const SETTINGS = {
  // mqtt-config.json
  brokerAddress: { label: 'MQTT broker address', help: 'IP or name of the broker. In Docker, host.docker.internal is the PC itself.' },
  brokerPort: { label: 'MQTT broker port', help: 'Usually 1883.' },
  clientIdSuffix: { label: 'MQTT client name', help: 'The gateway connects as <PC name>_<this>. Two gateways on one broker need different names.' },
  reconnectCooldownSeconds: { label: 'Reconnect wait (s)', help: 'After a failed connect to the broker, wait this long before trying again.' },
  username: { label: 'MQTT username', help: 'Login for the broker. Empty = connect without a login.' },
  password: { label: 'MQTT password', secret: true, help: 'Never shown: dots mean a password is saved. Type a new one to replace it, clear the field to remove it. Sent without encryption unless TLS is on.' },
  useTls: { label: 'MQTT over TLS (encrypted)', help: 'Encrypts the login and the data; the broker port is then usually 8883. The broker\'s certificate must be trusted by this PC (in Docker: the container), or come from the CA file below, and its name must match the broker address. Press connect to try. Applied on Restart Gateway.' },
  tlsCaFile: { label: 'TLS: company CA file', help: 'Empty = the CAs this PC already trusts. A broker with a company certificate: put the CA\'s .crt / .pem in the data Config folder (Docker: data\\Config) and type its file name here, or a full path.' },
  area: { label: 'Topic area', help: 'Topics the gateway makes itself start with svi/<area>/.' },
  keepaliveIntervalSeconds: { label: 'Keepalive every (s)', help: 'The gateway publishes {"alive":true} on svi/<area>/<gateway id>/keepalive.' },
  motionDeadband: { label: 'Motion: smallest change sent', help: 'A motion value is published again only after it moved at least this much.' },
  maxPublishRateMs: { label: 'IO / motion: ms between publishes', help: 'At most one publish per topic in this time (50 = 20 per second).' },
  // machine-config.json
  gatewayId: { label: 'Gateway id', help: 'Sent to MES in every record (gateway_id) and used in the keepalive topic.' },
  listenAddress: { label: 'Listen address', help: 'The IP of this PC that machines connect to. 0.0.0.0 = every address (needed in Docker).' },
  tcpPort: { label: 'Machine TCP port', help: 'Machines connect to listen address : this port.' },
  dataSource: { label: 'Default data source', help: 'Used for a message that has no data_source.' },
  protocolLabel: { label: 'Protocol label', help: 'Written into every MES record (protocol).' },
  mesUrls: { label: 'MES APIs', help: 'MES has one API per data group. Open a line to set its address and login; Test checks it without sending a record. A group without its own address or login uses the default. Each group is sent and retried on its own, so one API that is down or not open yet does not hold up the others. Applied on Restart Gateway.' },
  mesCaFile: { label: 'MES: company CA file (https)', help: "Empty = the CAs this PC already trusts. MES with a company certificate: put the CA's .crt / .pem in the data Config folder (Docker: data\\Config) and type its file name here, or a full path. Applied on Restart Gateway." },
  unregisteredMachines: { label: 'Machine without a tab', choices: [['hold', 'hold its data until it gets a tab'], ['shared', 'map it with the Template']] },
  unmappedTags: { label: 'Tag not in the mapping', choices: [['send', 'send the known tags, leave it out'], ['hold', 'hold the whole message until it is mapped']] },
  outboxKeepSentDays: { label: 'Keep sent records (days)', help: 'A day file is deleted when it is this many days old and everything in it was sent.' },
  mesStuckRowTries: { label: 'MES: tries before a stuck record moves to failed', help: 'A record MES keeps answering 500 or 413 moves to failed messages after this many tries in a row, once MES took other records meanwhile. At least 1. Applied on Restart Gateway.' },
  nameMatchThreshold: { label: 'Name matching: auto-map from score', help: 'An unknown tag whose name matches one MES column at least this well (0.5 to 1; 1 = the same name) is mapped automatically and marked "auto" in PointMap. Lower scores, or a column in several groups or already filled, are only suggested (Unidentified). Applied on Restart Gateway.' },
  alarmWebhookUrl: { label: 'Alarm webhook (Teams / Power Automate / Slack)', help: 'Empty = off. Problems are POSTed here as JSON (text, raised, cleared, active) when one starts or clears: MES unreachable for 5 min, records in MES failures, held machines, tabs created automatically, time warnings, over 10,000 records waiting. Applied on Restart Gateway.' },
  mappingPassword: { label: "Password to change a confirmed tab's mapping", secret: true, help: 'Asked by "Change mapping" (next to Save on a confirmed machine tab\'s PointMap; an automatic tab needs none). Empty = no password asked, only "are you sure?". Never shown: dots mean a password is saved. Applied on Restart Gateway.' },
  nameMatchSynonyms: { label: 'Name matching: words that mean the same', help: 'short=long, comma-separated, e.g. qty=quantity, sn=serial number. Used to compare tag names with MES columns. Applied on Restart Gateway.' },
  mesStuckRowMinutes: { label: 'MES: minutes before a stuck record moves to failed (when nothing else is sent)', help: 'Same, when MES took nothing else meanwhile (e.g. one machine alone). 1 to 10. Applied on Restart Gateway.' },
};

// MQTT broker / Gateway settings: settings grouped in panels, one per row with its help.
// Settings no panel names (a newer gateway, a hand-edited file) land in "Other settings".
const FORM_SECTIONS = {
  'mqtt-config': [
    ['i-broker', 'Broker', ['brokerAddress', 'brokerPort', 'username', 'password', 'useTls', 'tlsCaFile']],
    ['i-sliders', 'Topics and timing', ['clientIdSuffix', 'area', 'keepaliveIntervalSeconds', 'reconnectCooldownSeconds', 'motionDeadband', 'maxPublishRateMs']],
  ],
  'machine-config': [
    ['i-robot', 'Machines in', ['gatewayId', 'listenAddress', 'tcpPort', 'dataSource', 'protocolLabel', 'unregisteredMachines', 'unmappedTags']],
    ['i-db', 'MES', ['mesUrls', 'mesUrl', 'mesLogin', 'mesLogins', 'mesCaFile', 'outboxKeepSentDays', 'mesStuckRowTries', 'mesStuckRowMinutes']],
    ['i-tag', 'Name matching', ['nameMatchThreshold', 'nameMatchSynonyms']],
    ['i-bell', 'Alerts and safety', ['alarmWebhookUrl', 'mappingPassword']],
  ],
};

// MES APIs (GatewayConfig -> MES): the default address + login, then one per MES data group (MES has
// one API per group). Each is one closed line (name, address, login, last test) that opens to edit,
// so ten APIs don't fill the page. The file keeps mesUrl / mesLogin and mesUrls / mesLogins.<group>.
const MES_API_KEYS = new Set(['mesUrl', 'mesLogin', 'mesLogins']); // shown inside the list, not as rows
const LOGIN_TYPES = [['none', 'No login'], ['bearer', 'Bearer token'], ['basic', 'Username + password'], ['header', 'API key header']];
const loginName = login => {
  const type = (login?.type ?? '').trim().toLowerCase();
  return LOGIN_TYPES.find(([v]) => v === type)?.[1] ?? (type || 'No login');
};
const formRow = (label, input, help) => h('div', { class: 'form-row' },
  h('div', { class: 'label' }, h('b', { textContent: label })), input, help ? h('div', { class: 'help', textContent: help }) : null);

function mesApi(key, data, group) {
  const id = group ?? '';
  const name = group ?? 'Default';
  state.mesOpen ??= new Set();
  state.mesTests ??= {};
  const url = () => (group ? data.mesUrls?.[group] : data.mesUrl) ?? '';
  const login = () => (group ? data.mesLogins?.[group] : data.mesLogin) ?? { type: '' };
  const changed = () => { delete state.mesTests[id]; markDirty(key); refresh(); };
  const setUrl = text => {
    if (!group) data.mesUrl = text;
    else { data.mesUrls = { ...(data.mesUrls ?? {}) }; if (text) data.mesUrls[group] = text; else delete data.mesUrls[group]; }
    changed();
  };
  const setLogin = next => {
    if (!group) data.mesLogin = next;
    else { data.mesLogins = { ...(data.mesLogins ?? {}) }; if (next.type) data.mesLogins[group] = next; else delete data.mesLogins[group]; }
    changed();
  };

  const urlText = h('span', { class: 'api-url' });
  const loginText = h('span', { class: 'api-login' });
  const testText = h('span', { class: 'api-test' });
  const result = h('span', { class: 'api-result', role: 'status' });
  const refresh = () => {
    const address = url();
    urlText.textContent = address || (group ? 'default address' : 'none: groups without their own address wait');
    urlText.classList.toggle('quiet', !address);
    urlText.title = address;
    const own = group ? login().type : 'set';
    loginText.textContent = own ? loginName(login()) : 'default login';
    loginText.classList.toggle('quiet', !own);
    const test = state.mesTests[id];
    for (const el of [testText, result]) el.classList.toggle('ok', !!test?.ok), el.classList.toggle('bad', !!test && !test.ok);
    testText.textContent = test ? (test.ok ? '✓ OK' : '✗ failed') : '';
    testText.title = test?.text ?? '';
    result.textContent = test?.text ?? '';
  };

  const testBtn = h('button', {
    type: 'button', class: 'btn small ghost', textContent: 'Test',
    onclick: async () => {
      let settings;
      try { settings = buildBody(key); } catch (e) { toast(e.message, true); return; }
      testBtn.disabled = true;
      testBtn.textContent = 'Testing…';
      try { state.mesTests[id] = await request('/api/mes/test', { method: 'POST', body: JSON.stringify({ group: id, settings }) }); }
      catch (e) { state.mesTests[id] = { ok: false, text: e.message }; }
      finally { testBtn.disabled = false; testBtn.textContent = 'Test'; refresh(); }
    },
  });

  const body = h('div', { class: 'api-body' });
  const renderBody = () => {
    const own = login();
    const type = (own.type ?? '').trim().toLowerCase();
    const secret = { type: 'password', autocomplete: 'new-password' };
    const field = (label, prop, extra = {}) => formRow(label, h('input', {
      value: own[prop] ?? '', 'aria-label': `${label} for ${name}`, ...extra,
      oninput: e => setLogin({ ...login(), [prop]: prop === 'secret' ? e.target.value : e.target.value.trim() }),
    }));
    const options = group ? [['', `Same as default (${loginName(data.mesLogin)})`], ...LOGIN_TYPES] : LOGIN_TYPES;
    body.replaceChildren(...[ // replaceChildren would write null as text
      formRow('Address', h('input', {
        value: url(), 'aria-label': `MES address for ${name}`,
        placeholder: group ? 'empty = the default address' : 'e.g. https://mes-server/api/v1/machine-events',
        oninput: e => setUrl(e.target.value.trim()),
      }), group ? null : 'Empty = only groups with their own address are sent; the others wait in the outbox (nothing lost).'),
      formRow('Login', select(options, group ? type : type || 'none', v => { setLogin({ ...login(), type: v }); renderBody(); }, `Login for ${name}`)),
      type === 'bearer' ? field('Token', 'secret', secret) : null,
      type === 'basic' ? field('Username', 'username') : null,
      type === 'basic' ? field('Password', 'secret', secret) : null,
      type === 'header' ? field('Header name', 'header', { placeholder: 'X-API-Key' }) : null,
      type === 'header' ? field('Key', 'secret', secret) : null,
      h('div', { class: 'api-foot' }, testBtn, result)].filter(Boolean));
  };
  renderBody();
  refresh();

  const details = h('details', {
    class: 'api', open: state.mesOpen.has(id),
    ontoggle: () => (details.open ? state.mesOpen.add(id) : state.mesOpen.delete(id)),
  }, h('summary', {}, icon('i-right'), h('span', { class: 'api-name', textContent: name }), urlText, loginText, testText), body);
  details.style.setProperty('--c', group ? `var(--g-${group}, var(--accent))` : 'var(--accent)');
  return details;
}

function renderForm(view) {
  const key = state.view;
  const data = state.data[key];
  const sections = FORM_SECTIONS[key] ?? [];
  const listed = new Set(sections.flatMap(([, , names]) => names));
  const other = Object.keys(data).filter(n => !listed.has(n));
  let shown = 0;

  const row = name => {
    const value = data[name];
    const info = SETTINGS[name] ?? {};
    if (!(name in data)) return null;
    if (name === 'mesUrls') return mesApiRows(key, data, info);
    if (MES_API_KEYS.has(name)) return null; // inside the MES APIs list
    if (!matches(`${name} ${info.label ?? ''} ${typeof value === 'object' ? JSON.stringify(value) : value}`)) return null;
    shown++;
    let input;
    if (value !== null && typeof value === 'object') {
      input = h('input', { value: JSON.stringify(value), readOnly: true, title: 'Not editable here; edit the file itself.' });
    } else if (info.choices) {
      // A value from the file in another case (utc) still selects its choice; one the
      // gateway would refuse stays visible, marked, so it can be corrected.
      const current = String(value ?? '');
      const known = info.choices.find(([v]) => v.toLowerCase() === current.trim().toLowerCase());
      const options = known ? info.choices : [...info.choices, [current, `${current} (not valid)`]];
      input = select(options, known ? known[0] : current, v => { data[name] = v; markDirty(key); }, info.label);
    } else if (typeof value === 'boolean') {
      const label = h('span', { textContent: value ? 'On' : 'Off' });
      const sw = h('button', { type: 'button', class: 'switch', role: 'switch', 'aria-checked': String(value), 'aria-label': info.label ?? name,
        onclick: () => { data[name] = !data[name]; sw.setAttribute('aria-checked', String(data[name])); label.textContent = data[name] ? 'On' : 'Off'; markDirty(key); } });
      input = h('span', { class: 'toggle' }, sw, label);
    } else {
      const isNumber = state.numberKeys[key]?.includes(name);
      input = h('input', {
        ...(isNumber ? { type: 'number', step: 'any' } : {}),
        ...(info.secret ? { type: 'password', autocomplete: 'new-password' } : {}),
        value: value ?? '', 'aria-label': info.label ?? name,
        oninput: e => {
          const text = e.target.value;
          data[name] = isNumber && text !== '' ? Number(text) : text;
          markDirty(key);
        },
      });
    }
    return h('div', { class: 'form-row' },
      h('div', { class: 'label' }, h('b', { textContent: info.label ?? name }), info.label ? h('code', { textContent: name }) : null),
      input,
      info.help ? h('div', { class: 'help', textContent: info.help }) : null);
  };

  // MES APIs: the default, then every MES data group (and any other group the file names).
  function mesApiRows(key, data, info) {
    const groups = [...new Set([...Object.keys(state.mesSchema ?? {}), ...Object.keys(data.mesUrls ?? {}), ...Object.keys(data.mesLogins ?? {})])];
    const apis = [null, ...groups].filter(g => matches(`mes api address login ${info.label} ${g ?? 'default'} ${(g ? data.mesUrls?.[g] : data.mesUrl) ?? ''}`));
    shown += apis.length;
    return apis.length ? [h('div', { class: 'api-help', textContent: info.help }), ...apis.map(g => mesApi(key, data, g))] : null;
  }

  const panels = h('div', { class: 'forms' });
  for (const [ic, title, names] of [...sections, ...(other.length ? [['i-sliders', 'Other settings', other]] : [])]) {
    const rows = names.flatMap(row).filter(Boolean);
    if (rows.length) panels.append(h('article', { class: 'card grp' }, h('div', { class: 'head' }, icon(ic), title), ...rows));
  }

  if (key === 'mqtt-config') view.append(h('div', { class: 'statusbar quiet', id: 'mqttState' }));
  view.append(shown ? panels : h('div', { class: 'empty', textContent: 'No settings match the search.' }));
  showMqttState();
}

// MQTT broker page: the broker connection right now (from the gateway, not from the log).
function showMqttState() {
  const el = $('mqttState');
  if (!el) return;
  const connected = state.status?.mqttConnected;
  const saved = state.data['mqtt-config'] ?? {};
  el.className = `statusbar${connected == null ? ' quiet' : connected ? '' : ' bad'}`;
  el.replaceChildren(h('span', { class: `dot ${connected ? 'connected' : connected == null ? 'disconnected' : 'disconnected bad'}` }),
    h('b', { textContent: connected == null ? 'MQTT: unknown (gateway not running)' : connected ? 'MQTT: connected' : 'MQTT: disconnected' }),
    h('span', { textContent: `${saved.brokerAddress ?? ''}:${saved.brokerPort ?? ''}${saved.useTls ? ' · TLS' : ''} (saved settings)` }));
}

// ---------- actions ----------

// A view of the other kind of tab (e.g. the error badge opens Log) switches tab too.
function switchView(view) {
  if (GENERAL_VIEWS.has(view) && state.tab !== GENERAL) {
    state.lastView.machine = state.view;
    state.tab = GENERAL;
  } else if (!GENERAL_VIEWS.has(view) && state.tab === GENERAL) {
    state.lastView.general = state.view;
    state.tab = state.scope ? state.scope : SHARED;
    resetOutbox();
  }
  if (view === 'failed') state.failed = null; // load fresh
  state.view = view;
  render();
}

function addNewGroup() {
  const name = (prompt('New data group name (for example ENERGY):') ?? '')
    .trim().toUpperCase().replace(/[^A-Z0-9_]+/g, '_').replace(/^_+|_+$/g, '');

  if (name) {
    if (state.view === 'point-map' && !pointMapGroups().includes(name))
      state.extraGroups.push(name);
    state.group[state.view] = name;
  }

  render(); // also puts the picker back if the prompt was cancelled
}

// A new time rule for a group: opened in its PointMap panel to fill in.
function addTimestampRule(group) {
  state.data['timestamp-rules'][group] ??= { mode: 'SinglePoint', timestampField: 'record_timestamp' };
  markDirty('timestamp-rules');
  state.ruleOpen.add(group);
  if (state.view === 'point-map' && $(`grp-${group}`)) refreshGroup(group);
  else { state.focusGroup = group; switchView('point-map'); }
  toast(`Time rule added for ${group}. Check it and press save.`);
}

// What gets PUT for each file; throws with a readable message when something is missing.
function buildBody(key) {
  const data = structuredClone(state.data[key]);

  if (key === 'point-map') {
    const rows = data
      .map(r => ({ tag: (r.tag ?? '').trim(), dataGroup: r.dataGroup, fieldName: (r.fieldName ?? '').trim(), ...(r.auto != null ? { auto: r.auto } : {}) }))
      .filter(r => r.tag || r.fieldName);

    for (const [tag, edit] of Object.entries(state.unidEdits)) {
      const fieldName = edit.fieldName.trim();
      if (!edit.dataGroup && fieldName) throw new Error(`Pick a group for the unidentified tag '${tag}'.`);
      if (!edit.dataGroup) continue;
      const field = fieldName || tag.toLowerCase();
      // Like "assign it": a MES field that already has a tag gets both, combined with +.
      const existing = (mesColumns(edit.dataGroup) ?? []).includes(field)
        && rows.find(r => r.dataGroup === edit.dataGroup && r.fieldName === field && r.tag);
      if (!existing) rows.push({ tag, dataGroup: edit.dataGroup, fieldName: field });
      else if (!existing.tag.split('+').some(p => p.trim().toLowerCase() === tag.toLowerCase())) existing.tag += `+${tag}`;
    }

    const noTag = rows.find(r => !r.tag);
    if (noTag) throw new Error(`A row in ${noTag.dataGroup} has a fieldName but no incoming tag.`);

    const seen = new Set();
    for (const r of rows) {
      if (seen.has(r.tag.toLowerCase())) throw new Error(`Tag '${r.tag}' is listed twice. Tags must be unique.`);
      seen.add(r.tag.toLowerCase());
    }
    return rows;
  }

  if (key === 'timestamp-rules') {
    for (const [group, rule] of Object.entries(data)) {
      rule.mode ??= 'SinglePoint';
      const fields = rule.mode === 'Span' ? ['triggerField', 'startField', 'endField'] : ['timestampField'];
      const keep = ['mode', ...fields, ...(rule.mode === 'Span' ? ['previousField'] : []), 'sendEveryMs'];
      for (const k of Object.keys(rule)) if (!keep.includes(k)) delete rule[k];
      for (const k of keep) if (typeof rule[k] === 'string') rule[k] = rule[k].trim();

      if ('sendEveryMs' in rule && !(Number.isInteger(rule.sendEveryMs) && rule.sendEveryMs >= 0 && rule.sendEveryMs <= 3600000))
        throw new Error(`${group}: sendEveryMs must be a whole number from 0 to 3600000 (empty = 1000).`);

      if (rule.mode === 'Span' && fields.some(k => !rule[k]))
        throw new Error(`${group}: Span needs triggerField, startField and endField.`);
      if (rule.mode !== 'Span' && !rule.timestampField) delete rule.timestampField; // falls back to record_timestamp
      if (rule.mode === 'Span' && !rule.previousField) delete rule.previousField;   // falls back to previous_<triggerField>
    }
    return data;
  }

  for (const name of state.numberKeys[key] ?? [])
    if (!Number.isFinite(data[name])) throw new Error(`${name} needs a number.`);
  for (const [name, value] of Object.entries(data))
    if (typeof value === 'string') data[name] = value.trim();
  return data;
}

async function save() {
  // PointMap holds the group time rules too: one Save writes both files, each only when it changed.
  const both = ['point-map', 'timestamp-rules'].filter(k => state.dirty.has(k));
  const keys = state.view !== 'point-map' ? [state.view] : both.length ? both : ['point-map'];

  if (keys.includes('point-map') && state.group['point-map'] === UNID && !Object.values(state.unidEdits).some(e => e.dataGroup)) {
    toast('Pick a group for at least one unidentified tag first.', true);
    return;
  }

  const bodies = {};
  try { for (const key of keys) bodies[key] = buildBody(key); }
  catch (e) { toast(e.message, true); return; }

  // The MES format (shared by all machines) was edited on PointMap: save it first.
  if (keys.includes('point-map') && state.mesDirty) {
    try {
      await request('/api/config/mes-schema', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(state.mesSchema),
      });
      state.mesDirty = false;
    } catch (e) {
      toast(`MES format not saved: ${e.message}`, true);
      return;
    }
  }

  // On a machine tab: this machine only, similar machines, or all (only what changed is copied); one question for both files.
  const changes = Object.fromEntries(keys.map(key =>
    [key, MAPPING.has(key) && state.scope && state.saved[key] ? mappingChanges(key, state.saved[key], bodies[key]) : null]));
  const count = keys.reduce((n, key) => n + (changes[key]?.count ?? 0), 0);
  const targets = count ? await chooseSaveScope(count) : [];
  if (targets === null) return; // cancelled

  let restartToApply = false, reconnected = 0;
  for (const key of keys) {
    const result = await saveFile(key, bodies[key]);
    if (!result) { render(); return; }
    if (targets.length && changes[key]?.count) await applyToOthers(key, changes[key], targets);
    restartToApply ||= !!result.restartToApply;
    reconnected += result.reconnected ?? 0;
  }
  render();
  if (reconnected > 0) {
    toast(`Every held tag is mapped now: ${reconnected} connection(s) closed, the held messages are being sent again.`);
    await loadUnmapped();
  }
  askRestart(restartToApply);
}

// PUTs one file, as the copy it came from. Changed meanwhile (409: a tag mapped automatically, another save):
// PointMap / TimeStamp take the newer file plus only what was changed here, and save once more. null = not saved.
async function saveFile(key, body) {
  const url = `/api/config/${key}${scopeQuery(key)}`;
  let result, rebased = false;
  for (;;) {
    try {
      result = await request(url, { method: 'PUT', headers: ifMatch(state.versions[key]), body: JSON.stringify(body) });
      break;
    } catch (e) {
      if (e.status !== 409 || rebased || !MAPPING.has(key) || !state.saved[key]) {
        toast(`Not saved: ${e.message}`, true);
        return null;
      }
      try {
        const latest = await requestVersioned(url);
        body = rebase(state.saved[key], body, latest.body);
        state.versions[key] = latest.version;
        rebased = true;
      } catch (e2) {
        toast(`Not saved: ${e2.message}`, true);
        return null;
      }
    }
  }
  state.versions[key] = result.version;
  if (rebased) toast('Saved. The file had changed meanwhile (e.g. a tag mapped automatically): those rows were kept.');

  state.data[key] = body;
  if (MAPPING.has(key)) state.saved[key] = structuredClone(body);
  state.dirty.delete(key);
  if (key === 'point-map') {
    state.unidEdits = {};
    state.extraGroups = state.extraGroups.filter(g => !body.some(r => r.dataGroup === g));
    await loadUnidentified();
  }
  return result;
}

// What a save of PointMap / TimeStamp changed, so that only this is copied to other machines.
// PointMap by tag: rows added or moved (set) and tags taken out (removed). TimeStamp by group.
function mappingChanges(key, before, after) {
  if (key === 'point-map') {
    const old = new Map(before.map(r => [r.tag.toLowerCase(), r]));
    const now = new Set(after.map(r => r.tag.toLowerCase()));
    const set = after.filter(r => { const o = old.get(r.tag.toLowerCase()); return !o || o.dataGroup !== r.dataGroup || o.fieldName !== r.fieldName; });
    const removed = before.filter(r => !now.has(r.tag.toLowerCase())).map(r => r.tag);
    return { set, removed, count: set.length + removed.length };
  }
  const set = Object.entries(after).filter(([g, rule]) => JSON.stringify(rule) !== JSON.stringify(before[g]));
  const removed = Object.keys(before).filter(g => !(g in after));
  return { set, removed, count: set.length + removed.length };
}

// The newer file plus only what this person changed since loading `before` (`mine`): PointMap rows by
// tag (rows added meanwhile, e.g. mapped automatically, stay with their auto mark), TimeStamp by group.
function rebase(before, mine, latest) {
  if (Array.isArray(mine)) {
    const id = r => String(r.tag).toLowerCase();
    const sig = r => [r.tag, r.dataGroup, r.fieldName, r.auto ?? ''].join('\u0001');
    const old = new Map(before.map(r => [id(r), sig(r)]));
    const kept = new Set(mine.map(id));
    const changed = mine.filter(r => old.get(id(r)) !== sig(r));
    const drop = new Set([...before.filter(r => !kept.has(id(r))).map(id), ...changed.map(id)]);
    return [...latest.filter(r => !drop.has(id(r))), ...changed];
  }
  const next = { ...latest };
  for (const k of Object.keys(before)) if (!(k in mine)) delete next[k];
  for (const [k, v] of Object.entries(mine)) if (JSON.stringify(v) !== JSON.stringify(before[k])) next[k] = v;
  return next;
}

// Another machine's PointMap / TimeStamp with these changes; its other rows stay as they are.
function applyChanges(key, data, changes) {
  if (key === 'point-map') {
    const drop = new Set([...changes.removed, ...changes.set.map(r => r.tag)].map(t => t.toLowerCase()));
    return [...data.filter(r => !drop.has(r.tag.toLowerCase())),
      ...changes.set.map(({ tag, dataGroup, fieldName }) => ({ tag, dataGroup, fieldName }))];
  }
  const next = { ...data };
  for (const g of changes.removed) delete next[g];
  for (const [g, rule] of changes.set) next[g] = rule;
  return next;
}

// Asks where a save on a machine tab goes. Resolves to the other mappings to change too
// (machine ids; '' = the Template): [] = this machine only, null = cancelled.
function chooseSaveScope(count) {
  const me = state.scope;
  const others = state.machineConfigs.filter(m => m.readable && m.machineId !== me).map(m => m.machineId);
  const similar = others.filter(id => familyOf(id) === familyOf(me));
  const names = list => list.length > 8 ? `${list.slice(0, 8).join(', ')} … (+${list.length - 8} more)` : list.join(', ');
  return new Promise(resolve => {
    const dialog = h('dialog', { class: 'scope-dialog' });
    const done = targets => { dialog.close(); dialog.remove(); resolve(targets); };
    dialog.append(
      h('p', { class: 'dlg-title', textContent: `Save ${count} change(s) for…` }),
      h('div', { class: 'scope-choices' },
        h('button', { type: 'button', class: 'btn primary', textContent: `Just ${me}`, onclick: () => done([]) }),
        h('button', { type: 'button', class: 'btn', textContent: `Similar machines (${similar.length})`, disabled: !similar.length, onclick: () => done(similar) }),
        h('button', { type: 'button', class: 'btn', textContent: `All machines (${others.length}) + Template`, onclick: () => done([...others, '']) })),
      h('p', { class: 'dlg-small', textContent: similar.length ? `Similar (same name as ${me}, numbers aside): ${names(similar)}` : 'No other machine has a similar name.' }),
      h('p', { class: 'dlg-small', textContent: 'Only what you changed here is copied; the other machines keep the rest of their settings.' }),
      h('div', { class: 'dlg-buttons' }, h('button', { type: 'button', class: 'btn ghost', textContent: 'Cancel', onclick: () => done(null) })));
    dialog.addEventListener('cancel', e => { e.preventDefault(); done(null); });
    document.body.append(dialog);
    dialog.showModal();
  });
}

async function applyToOthers(key, changes, targets) {
  const failed = [];
  for (const id of targets) {
    const query = id ? `?machine=${encodeURIComponent(id)}` : '';
    try {
      for (let attempt = 0; ; attempt++) { // changed between reading and saving: read it again once
        const { body: data, version } = await requestVersioned(`/api/config/${key}${query}`);
        try {
          await request(`/api/config/${key}${query}`, { method: 'PUT', headers: ifMatch(version), body: JSON.stringify(applyChanges(key, data, changes)) });
          break;
        } catch (e) {
          if (e.status !== 409 || attempt) throw e;
        }
      }
    } catch (e) {
      failed.push(`${id || 'Template'} (${e.message})`);
    }
  }
  const done = targets.length - failed.length;
  toast(failed.length ? `Also saved for ${done} of ${targets.length}. Not saved: ${failed.join('; ')}` : `Also saved for ${done} other mapping(s).`, failed.length > 0);
}

function askRestart(restartToApply) {
  state.restartToApply = restartToApply;
  $('restartText').textContent = restartToApply
    ? 'These settings take effect after a restart. Restart the gateway now?'
    : 'Already applied live. Restart the gateway anyway?';
  if (!$('restartDialog').open) $('restartDialog').showModal();
}

async function restartGateway() {
  $('restartDialog').close();
  $('busyText').textContent = 'Restarting gateway…';
  $('busy').hidden = false;

  let before = 0;
  try {
    before = (await request('/api/status')).run;
    await request('/api/gateway/restart', { method: 'POST' });
  } catch (e) {
    $('busy').hidden = true;
    toast(`Restart failed: ${e.message}`, true);
    return;
  }

  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    await sleep(500);
    try {
      const status = await request('/api/status');
      showStatus(status);
      if (status.state === 'running' && status.run > before) {
        $('busy').hidden = true;
        toast('Gateway restarted and running.');
        return;
      }
      if (status.state === 'error') {
        $('busy').hidden = true;
        toast(`Gateway could not start: ${status.lastError}`, true);
        return;
      }
    } catch {
      // config server busy for a moment; keep asking
    }
  }

  $('busy').hidden = true;
  toast('The gateway did not report back within 20 s. Check its console window.', true);
}

// ---------- gateway status (top bar, right) ----------

function showStatus(status) {
  state.status = status;
  // The MQTT banner follows the real connection (mqttConnected), not log lines.
  state.problems.mqtt = status.mqttConnected === false ? (state.problems.mqtt ?? new Date().toISOString()) : null;
  showMqttState();
  $('gwStatus').className = `gw-status ${status.state}`;
  const since = status.startedAt ? new Date(status.startedAt).toLocaleTimeString() : '';
  $('gwText').textContent =
    status.state === 'running' ? `Running · TCP ${status.tcpPort}` :
    status.state === 'error' ? `Stopped: ${status.lastError}` :
    `${status.state[0].toUpperCase()}${status.state.slice(1)}…`;
  $('gwText').title = status.state === 'running' ? `Gateway running since ${since}` : status.state === 'error' ? 'The gateway tries again by itself.' : '';

  // Last 10 s, per second: lines in, MQTT out, MES delivered, rows waiting for MES (all of it on hover).
  const s = status.stats;
  const n = v => Math.round(v).toLocaleString();
  const rate = v => v < 10 ? v.toFixed(1) : n(v); // 1 message in 10 s shows as 0.1, not 0
  const parts = [];
  if (status.mqttConnected != null)
    parts.push('MQTT ', h('b', { class: status.mqttConnected ? 'ok' : 'bad', textContent: status.mqttConnected ? 'connected' : 'down' }));
  if (s && s.at)
    parts.push(...(parts.length ? [' · '] : []), 'MES ', h('b', { textContent: `${rate(s.mesDeliveredPerSec)}/s` }), ' · backlog ', h('b', { textContent: s.backlog < 0 ? '?' : n(s.backlog) }));
  $('gwStats').replaceChildren(...parts);
  $('gwStats').title = s && s.at
    ? `Last 10 s: in ${rate(s.inPerSec)}/s · MQTT ${rate(s.mqttPerSec)}/s · MES ${rate(s.mesDeliveredPerSec)}/s · backlog ${s.backlog < 0 ? '?' : n(s.backlog)}` +
      (s.rejectedPerSec > 0 ? ` · ${rate(s.rejectedPerSec)}/s rejected` : '') +
      (s.duplicatePerSec > 0 ? ` · ${rate(s.duplicatePerSec)}/s repeats ignored` : '') +
      (s.mqttLostPerSec > 0 ? ` · MQTT lost ${rate(s.mqttLostPerSec)}/s` : '')
    : '';
}

async function pollStatus() {
  let status;
  try {
    status = await request('/api/status');
    state.unreachable = false;
    showStatus(status);
  } catch {
    state.unreachable = true;
    renderAlerts();
    $('gwStatus').className = 'gw-status error';
    $('gwText').textContent = 'Cannot reach the gateway.';
    return;
  }
  renderAlerts(); // gateway stopped / running, and the backlog in the MES line

  // Machines sending without a tab, or with tags not mapped yet (data held): keep the alerts current.
  if (status.held > 0 || state.held.length) await loadHeld();
  if (status.unmapped > 0 || state.unmapped.length) await loadUnmapped();

  // Messages MES rejected as invalid: alert when new ones arrive.
  const failed = status.failed ?? 0;
  if (failed !== state.failedCount) {
    if (failed > state.failedCount)
      toast(`MES rejected ${(failed - state.failedCount).toLocaleString()} message(s) as invalid. See General → MES failures.`, true);
    state.failedCount = failed;
    if (state.view === 'failed') await loadFailed();
    else renderTabs();
  }

  // Keep the Unidentified count fresh without redrawing what the user is typing in.
  await loadUnidentified();
  const count = $('unidCount');
  if (count) {
    const n = unidentifiedList().length;
    count.textContent = String(n);
    count.className = `pill ${n ? 'count' : 'quiet'}`;
  }
}

// ---------- wiring ----------

document.querySelectorAll('.nav').forEach(button =>
  button.addEventListener('click', () => switchView(button.dataset.view)));

// ---------- column widths: drag a grip line (or ← / →), double-click for the default ----------
// Remembered in this browser only (a per-viewer convenience; the page works without it).
const COLUMN_DEFAULTS = { tabs: 260 };
const MIN_COLUMN = 120, MIN_PANEL = 300;
const columnWidth = col => parseFloat(getComputedStyle($('window')).getPropertyValue(`--w-${col}`)) || COLUMN_DEFAULTS[col];

function setColumn(col, px) {
  const win = $('window');
  const fixed = win.clientWidth - $('window').querySelector('.panel').offsetWidth
    - Object.keys(COLUMN_DEFAULTS).reduce((sum, c) => sum + columnWidth(c), 0); // grips + padding
  const others = Object.keys(COLUMN_DEFAULTS).filter(c => c !== col).reduce((sum, c) => sum + columnWidth(c), 0);
  const width = Math.round(Math.max(MIN_COLUMN, Math.min(px, win.clientWidth - fixed - others - MIN_PANEL)));
  win.style.setProperty(`--w-${col}`, `${width}px`);
  win.querySelector(`.grip[data-col="${col}"]`).setAttribute('aria-valuenow', String(width));
}

function saveColumns() {
  try { localStorage.setItem('gateway.columns', JSON.stringify(Object.fromEntries(Object.keys(COLUMN_DEFAULTS).map(c => [c, columnWidth(c)])))); }
  catch { /* private window or blocked storage: widths just aren't remembered */ }
}

try {
  const saved = JSON.parse(localStorage.getItem('gateway.columns') ?? '{}');
  for (const col of Object.keys(COLUMN_DEFAULTS))
    if (Number.isFinite(saved[col])) setColumn(col, saved[col]);
} catch { /* nothing remembered */ }
window.addEventListener('resize', () => Object.keys(COLUMN_DEFAULTS).forEach(c => setColumn(c, columnWidth(c))));

document.querySelectorAll('.grip').forEach(grip => {
  const col = grip.dataset.col;
  grip.addEventListener('pointerdown', e => {
    e.preventDefault();
    grip.setPointerCapture(e.pointerId);
    const startX = e.clientX, startWidth = columnWidth(col);
    grip.classList.add('dragging');
    document.body.classList.add('resizing');
    const move = ev => setColumn(col, startWidth + ev.clientX - startX);
    const up = () => {
      grip.removeEventListener('pointermove', move);
      grip.classList.remove('dragging');
      document.body.classList.remove('resizing');
      saveColumns();
    };
    grip.addEventListener('pointermove', move);
    grip.addEventListener('pointerup', up, { once: true });
    grip.addEventListener('pointercancel', up, { once: true });
  });
  grip.addEventListener('dblclick', () => { setColumn(col, COLUMN_DEFAULTS[col]); saveColumns(); });
  grip.addEventListener('keydown', e => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    e.preventDefault();
    setColumn(col, columnWidth(col) + (e.key === 'ArrowRight' ? 16 : -16));
    saveColumns();
  });
});

$('search').addEventListener('input', e => { state.search = e.target.value; render(); });
$('findMachine').addEventListener('input', e => { state.findMachine = e.target.value; renderTabs(); });
$('search').addEventListener('keydown', e => { if (e.key === 'Enter') render(); });

$('saveBtn').addEventListener('click', save);
$('reassignBtn').addEventListener('click', () => state.scope && reassignTab(state.scope));
$('connectBtn').addEventListener('click', testMqtt);

// Connect on MQTTConfig: tries the broker with what is typed there now, also before saving.
// The gateway's running connections stay as they are until the settings are saved.
async function testMqtt() {
  let body;
  try { body = buildBody('mqtt-config'); } catch (e) { toast(e.message, true); return; }
  const btn = $('connectBtn');
  btn.disabled = true;
  btn.querySelector('span').textContent = 'Connecting…';
  try {
    const r = await request('/api/mqtt/test', { method: 'POST', body: JSON.stringify(body) });
    const unsaved = state.dirty.has('mqtt-config') ? ' Save to use these settings.' : '';
    toast(r.connected ? `MQTT: connected to ${r.broker}${r.tls ? ' with TLS' : ''}.${unsaved}` : `MQTT: not connected to ${r.broker}: ${r.error}`, !r.connected);
  } catch (e) {
    toast(`MQTT test failed: ${e.message}`, true);
  } finally {
    btn.disabled = false;
    btn.querySelector('span').textContent = 'Connect';
  }
}
$('errBadge').addEventListener('click', () => { state.errorsOnly = true; switchView('log'); });
$('restartNow').addEventListener('click', restartGateway);
$('restartLater').addEventListener('click', () => {
  $('restartDialog').close();
  toast(state.restartToApply ? 'Saved. Restart the gateway later to apply it.' : 'Saved.');
});

window.addEventListener('beforeunload', e => {
  if (state.dirty.size) { e.preventDefault(); e.returnValue = ''; }
});

(async () => {
  try {
    await loadAll();
    render();
  } catch (e) {
    $('view').replaceChildren(h('div', { class: 'empty', textContent: `Could not load the configuration: ${e.message}` }));
  }
  pollStatus();
  pollLogs();
  pollMachines();
  setInterval(() => { pollStatus(); pollLogs(); pollMachines(); }, 4000);
})();
