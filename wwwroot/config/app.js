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
  lastView: { general: 'mqtt-config', machine: 'point-map' }, // reopened when coming back to that kind of tab
  view: 'mqtt-config',
  data: {},                 // file key -> data as loaded / being edited
  saved: {},                // PointMap / TimeStamp as last loaded or saved: what a save changed is copied to other machines
  numberKeys: {},           // file key -> keys that must stay numbers (form views)
  dirty: new Set(),         // file keys with unsaved edits
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

// ---------- small helpers ----------

const $ = id => document.getElementById(id);

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
  const res = await fetch(url, { cache: 'no-store', ...options, headers: { 'Content-Type': 'application/json', ...options?.headers } });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.error || `HTTP ${res.status}`);
  return body;
}

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
  const data = await request(`/api/config/${key}${scopeQuery(key)}`);
  state.data[key] = data;
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
const STATUS_WORDS = { connected: 'connected', idle: 'connected, no message for 60 s', disconnected: 'not connected' };
function connectionOf(machineId) {
  const m = state.machines?.machines?.find(x => x.machineId.toLowerCase() === machineId.toLowerCase());
  return m?.status in STATUS_WORDS ? m.status : 'disconnected';
}

function renderTabs() {
  // The machine whose buttons the middle column shows; stays marked while a General view is open.
  const selected = state.tab === GENERAL ? (state.scope || SHARED) : state.tab;
  const tab = (id, label, opts = {}) => {
    const el = h('div', {
      class: `tab${selected === id ? ' active' : ''}${opts.fixed ? ' fixed' : ''}${opts.extra ? ' ' + opts.extra : ''}`,
      role: 'tab', tabIndex: 0, title: opts.title ?? label,
      onclick: () => switchTab(id),
      onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); switchTab(id); } },
    }, h('span', { class: 'tab-name', textContent: label }));
    el.setAttribute('aria-selected', String(selected === id));
    if (opts.auto)
      el.append(h('span', { class: 'tab-auto', textContent: 'auto', title: 'Tab created automatically: check it, then confirm it on its PointMap' }));
    if (opts.status)
      el.append(h('span', { class: `dot ${opts.status}`, role: 'img', 'aria-label': STATUS_WORDS[opts.status], title: STATUS_WORDS[opts.status] }));
    if (opts.close)
      el.append(h('button', {
        type: 'button', class: 'tab-x', textContent: '×',
        title: holdPolicy() ? `Close: ${label}'s data is held until it gets a tab again` : `Close: ${label} goes back to the shared mapping`,
        'aria-label': `Close ${label}`, onclick: e => { e.stopPropagation(); removeMachine(id); },
      }));
    return el;
  };

  $('tabs').replaceChildren(...[
    h('h1', { class: 'tabs-title', textContent: 'Machines' }),
    tab(SHARED, unmappedFor('').length ? `⚠ ${sharedLabel()}` : sharedLabel(), { fixed: true, extra: unmappedFor('').length ? 'broken' : '', title: holdPolicy() ? 'Template: what "+" copies into a new machine tab. Machines without a tab are held, not mapped with this.' : 'The shared mapping: every machine that has no own tab' }),
    ...state.machineConfigs.map(m => {
      const waiting = unmappedFor(m.machineId);
      const alert = !m.readable || waiting.length > 0;
      const status = connectionOf(m.machineId);
      return tab(m.machineId, alert ? `⚠ ${m.machineId}` : m.machineId, {
        close: true, extra: alert ? 'broken' : '', status, auto: m.autoCopiedFrom != null,
        title: !m.readable ? `${m.machineId}: its files can't be read (${m.error})`
          : waiting.length ? `${m.machineId}: data held, tags not mapped yet: ${[...new Set(waiting.flatMap(u => u.tags))].join(', ')}`
          : `${m.machineId} (${STATUS_WORDS[status]}): its own mapping and outbox`,
      });
    }),
    h('button', { type: 'button', class: 'tab-add', textContent: '+', title: 'Give a machine its own tab and mapping', 'aria-label': 'Add machine', onclick: addMachine }),
    // Machines sending without a tab: their data waits (held) until someone registers them.
    state.held.length ? h('button', {
      type: 'button', class: 'tab-alert', textContent: `⚠ ${state.held.length} not registered`,
      title: `Sending without a tab, data held: ${state.held.map(m => m.machineId).join(', ')}. Click to register.`,
      onclick: () => switchView('machines'),
    }) : null,
    // Messages MES rejected as invalid: kept, not retried, until someone re-sends or discards them.
    state.failedCount ? h('button', {
      type: 'button', class: 'tab-alert', textContent: `⚠ ${state.failedCount.toLocaleString()} rejected by MES`,
      title: 'MES said these messages are invalid. They are kept (not retried) until you re-send or discard them. Click to see why.',
      onclick: () => switchView('failed'),
    }) : null,
  ].filter(Boolean));
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
    ? `${id} registered. Its adapter was reconnected and sends the held data again. Map the tags it sent under ⚠ Unidentified.`
    : `${id} registered. Its held data arrives with its adapter's next resend. Map the tags it sent under ⚠ Unidentified.`);
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
    hint.append(`${auto.machineId}'s tab was created automatically, as a copy of ${auto.autoCopiedFrom || 'the Template'}. Check its mapping, then `,
      h('button', { type: 'button', class: 'link', textContent: '✓ confirm', onclick: () => confirmAutoTab(auto.machineId) }), '.');
    return;
  }
  if (broken) {
    hint.append(`${broken.machineId}'s files can't be read, so its lines are held until they are fixed: ${broken.error}`);
    return;
  }
  if (!waiting.length) return;
  const tags = [...new Set(waiting.flatMap(u => u.tags))];
  const lines = waiting.reduce((n, u) => n + u.lines, 0);
  hint.append(
    `Held: ${lines.toLocaleString()} message(s) from ${waiting.map(u => u.machineId).join(', ')}, because this mapping doesn't know: ${tags.join(', ')}. ` +
    'Map each one or set it to ignore, then save: the held messages are sent again. ',
    h('button', { type: 'button', class: 'link', textContent: 'show them', onclick: () => { state.group['point-map'] = UNID; switchView('point-map'); } }));
}

async function confirmAutoTab(id) {
  try { await request(`/api/machine-configs/confirm?machine=${encodeURIComponent(id)}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' }); }
  catch (e) { toast(`Not confirmed: ${e.message}`, true); return; }
  await loadMachineConfigs();
  render();
  toast(`${id}'s tab is confirmed.`);
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
  const mapped = new Set(state.data['point-map'].flatMap(row => (row.tag ?? '').toLowerCase().split('+').map(t => t.trim())));
  return state.unidentified.filter(u => !mapped.has(u.tag.toLowerCase()));
}

// ---------- rendering ----------

function updateNav() {
  $('tabTitle').textContent = state.scope || sharedLabel(); // the machine column's buttons, also while General is open
  document.querySelectorAll('.nav').forEach(button => {
    button.classList.toggle('active', button.dataset.view === state.view);
    button.querySelector('.dirty')?.remove();
    if (state.dirty.has(button.dataset.view))
      button.append(h('span', { class: 'dirty', textContent: ' •', title: 'Unsaved changes' }));
  });
}

function fillPicker(options, selected) {
  $('picker').replaceChildren(...options.map(([value, text]) => h('option', { value, textContent: text })));
  $('picker').value = selected;
  $('pickerRow').hidden = false;
}

function render() {
  updateNav();
  $('pickerHint').replaceChildren();
  $('pickerRow').hidden = true;
  const view = $('view');
  view.replaceChildren();

  const isLog = state.view === 'log';
  const readOnly = !FILES[state.view]; // Log / Machines / Outbox: nothing to save
  $('saveBtn').hidden = readOnly;
  renderTabs();
  renderScopeHint();

  if (state.view === 'point-map') renderPointMap(view);
  else if (state.view === 'timestamp-rules') renderTimestamp(view);
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
    state.scope ? `Applies as soon as you save. Only ${state.scope} uses this mapping. MES fields: the MES format, shared by all machines.` :
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

  // First: machines sending without a tab. Their data is held (not processed, not
  // answered) until someone registers them here.
  if (state.held.length) {
    view.append(h('h3', { class: 'sub held-title', textContent: `⚠ Not registered: ${state.held.length} machine(s) sending without a tab, data held` }));
    view.append(h('p', { class: 'section-note', textContent:
      'Their adapters keep the data. Register a machine: it gets its own tab copied from the mapping you pick, its adapter is reconnected and sends the held data again, and the tags it sent appear under ⚠ Unidentified to map.' }));
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
            h('button', { type: 'button', class: 'primary', textContent: 'register', onclick: () => registerMachine(m.machineId, from.value) })));
      })));
    view.append(h('h3', { class: 'sub', textContent: 'All machines' }));
  }

  const machines = data.machines.filter(m => matches(`${m.machineId} ${m.connection} ${m.status} ${m.dataSources.map(d => d.name).join(' ')}`));
  const online = data.machines.filter(m => m.status !== 'disconnected').length;
  const openConnections = data.connections.filter(c => c.open).length;
  view.append(h('p', { class: 'section-note', textContent:
    `${data.machines.length} machines (${online} connected) · ${openConnections} open connections` }));

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
        class: m.held ? 'small bad' : (m.duplicates || m.badTimes) ? 'small warn' : 'small',
        textContent: issues([[m.held, 'held'], [m.duplicates, 'repeats'], [m.badTimes ?? 0, 'time warnings']]),
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
  view.append(h('h3', { class: 'sub', textContent: 'Messages received (kept 7 days)' }),
    h('div', { class: 'outbox-tools msg-search' },
      field('date', 'day', 'day'), field('time', 'from', 'from time'), h('span', { textContent: 'to' }), field('time', 'to', 'to time'),
      select([['', 'all machines'], ...names.map(n => [n, n])], s.machine, v => { s.machine = v; }, 'machine'),
      h('button', { type: 'button', class: 'primary', textContent: 'Search', onclick: searchMessages })));
  if (s.error) { view.append(h('p', { class: 'hint', textContent: s.error })); return; }
  const r = s.result;
  if (!r) return;
  view.append(h('p', { class: 'section-note', textContent:
    `${r.total.toLocaleString()} line(s) on ${s.day}, ${s.from}–${s.to}${s.machine ? ` from ${s.machine}` : ''} · stored days: ${(r.storedBytes / 1048576).toFixed(1)} MB` }));
  if (!r.total) return;
  view.append(h('div', { class: 'msg-results' },
    table(['machine', 'lines'], r.machines.map(m => h('tr', {}, h('td', { textContent: m.machine }), h('td', { class: 'num', textContent: m.count.toLocaleString() })))),
    table(['hour', 'lines'], r.hours.map(x => h('tr', {},
      h('td', { textContent: `${x.hour.slice(11, 13)}:00–${x.hour.slice(11, 13)}:59` }), h('td', { class: 'num', textContent: x.count.toLocaleString() }))))));
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
const familyOf = id => (id.includes('-') ? id.slice(0, id.indexOf('-')) : id.replace(/\d+$/, '')).toLowerCase();

// A machine named like an existing tab most likely is the same model: suggest copying
// that tab (a confirmed one first). Else the template.
function guessCopyFrom(machineId) {
  const family = familyOf(machineId);
  const same = state.machineConfigs.filter(c => c.readable && family && familyOf(c.machineId) === family);
  return (same.find(c => c.autoCopiedFrom == null) ?? same[0])?.machineId ?? '';
}

async function pollMachines() {
  try { state.machines = await request('/api/machines'); } catch { return; }
  renderTabs(); // the connection dots in the machine list
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
  const machineInput = h('input', { value: o.machine, placeholder: 'all machines', 'aria-label': 'machine',
    onchange: e => { o.machine = e.target.value.trim(); reload(); } });
  machineInput.setAttribute('list', 'outboxMachines'); // read-only as a property
  const machineList = h('datalist', { id: 'outboxMachines' }, ...knownMachines.map(m => h('option', { value: m })));
  const groupSelect = select([['', 'all groups'], ...groups.map(g => [g, g])], o.group, v => { o.group = v; reload(); }, 'group');
  const sourceInput = h('input', { value: o.source, placeholder: 'all data sources', 'aria-label': 'data source',
    onchange: e => { o.source = e.target.value.trim(); reload(); } });
  const statusSelect = select([['', 'all'], ['sent', 'sent to MES'], ['waiting', 'waiting'], ['failed', 'rejected by MES'], ['discarded', 'discarded']],
    o.status, v => { o.status = v; reload(); }, 'status');

  view.append(h('div', { class: 'outbox-tools' }, scopeSelect, daySelect, machineInput, machineList, groupSelect, sourceInput, statusSelect,
    h('button', { type: 'button', textContent: 'refresh', onclick: () => { o.days = null; render(); } })));

  if (!o.days.length) {
    view.append(h('div', { class: 'empty', textContent: 'The outbox is empty.' }));
    return;
  }

  const rows = o.rows.filter(r => matches(`${r.machineId} ${r.dataSource} ${r.group} ${r.preview}`));
  view.append(rows.length ? table(
    ['', 'created', 'machine', 'data source', 'group', 'MES', 'size', 'payload'],
    rows.flatMap(r => {
      const open = o.open[r.id];
      const tr = h('tr', { class: 'clickable', title: 'Click to show the whole envelope', onclick: () => toggleRow(r.id) },
        h('td', { textContent: open ? '▾' : '▸' }),
        h('td', { class: 'nowrap', textContent: new Date(r.createdAt).toLocaleTimeString() }),
        h('td', { class: 'strong', textContent: r.machineId ?? '—' }),
        h('td', { textContent: r.dataSource ?? '—' }),
        h('td', { textContent: r.group }),
        r.sent ? h('td', { class: 'nowrap', textContent: `sent ${new Date(r.sentAt).toLocaleTimeString()}` })
          : r.discardedAt ? h('td', { class: 'nowrap', textContent: 'discarded', title: `Discarded ${localTime(r.discardedAt)}: ${r.discardReason}\nMES said: ${r.error ?? ''}` })
          : r.failedAt ? h('td', { class: 'nowrap bad', textContent: 'rejected', title: `MES said: ${r.error}\nRe-send or discard it under General → MES failures.` })
          : h('td', { class: 'nowrap warn', textContent: 'waiting' }),
        h('td', { class: 'num', textContent: r.size >= 1024 ? `${(r.size / 1024).toFixed(1)} KB` : `${r.size} B` }),
        h('td', { class: 'small preview', textContent: r.preview }));
      return open ? [tr, h('tr', {}, h('td', { colSpan: 8 }, h('pre', { class: 'json', textContent: open })))] : [tr];
    })
  ) : h('div', { class: 'empty', textContent: o.loading ? 'Loading…' : 'No rows for these filters.' }));

  if (o.more)
    view.append(h('button', { type: 'button', class: 'more', textContent: 'load older rows', onclick: () => loadOutbox(true) }));
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
  view.append(h('div', { class: 'outbox-tools' },
    h('button', { type: 'button', class: 'primary', textContent: picked.length ? `re-send ${picked.length} ticked` : 're-send all',
      disabled: !f.total, onclick: () => failedAction('resend', picked.length ? refs(picked) : null) }),
    h('button', { type: 'button', textContent: picked.length ? `discard ${picked.length} ticked…` : 'discard all…',
      disabled: !f.total, onclick: () => failedAction('discard', picked.length ? refs(picked) : null) }),
    h('button', { type: 'button', textContent: 'refresh', onclick: () => loadFailed() })));

  if (!f.total) {
    view.append(h('div', { class: 'empty', textContent: 'Nothing rejected by MES.' }));
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
        h('td', { textContent: open ? '▾' : '▸' }),
        h('td', { class: 'strong', textContent: r.machineId ?? '—' }),
        h('td', { textContent: r.group }),
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
  updateErrorBadge();

  const onlyErrors = h('input', {
    type: 'checkbox', checked: state.errorsOnly,
    onchange: e => { state.errorsOnly = e.target.checked; render(); },
  });
  view.append(h('label', { class: 'log-tools' }, onlyErrors, 'Show problems only'));

  const lines = state.logs.filter(l => (!state.errorsOnly || l.isError) && matches(l.text));
  const box = h('div', { class: 'log', id: 'logBox' });
  if (!lines.length)
    box.append(h('div', { class: 'empty', textContent: state.logs.length ? 'No lines match.' : 'No log lines yet.' }));
  for (const line of lines)
    box.append(h('div', { class: line.isError ? 'log-line err' : 'log-line' },
      h('span', { class: 't', textContent: new Date(line.time).toLocaleTimeString() }), line.text));

  view.append(box);
  box.scrollTop = box.scrollHeight;
}

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
  const unseen = state.errorCount - state.seenErrors;
  $('errBadge').hidden = unseen <= 0;
  $('errBadge').textContent = `⚠ ${unseen} new problem${unseen === 1 ? '' : 's'} · view log`;
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

// PointMap: incoming tag <-> fieldName, one data group at a time.
function renderPointMap(view) {
  const groups = pointMapGroups();
  const unid = unidentifiedList();

  let group = state.group['point-map'];
  if (group !== ALL && group !== UNID && !groups.includes(group)) group = groups[0] ?? ALL;
  state.group['point-map'] = group;

  fillPicker([
    [ALL, 'All groups'],
    ...groups.map(g => [g, g]),
    [UNID, `⚠ Unidentified (${unid.length})`],
    [NEW, '+ New group…'],
  ], group);

  if (group === UNID) {
    renderUnidentified(view, unid, groups);
    return;
  }

  if (group !== ALL && !TWIN_GROUPS.includes(group) && !state.data['timestamp-rules'][group]) {
    $('pickerHint').replaceChildren(
      `No timestamp rule for ${group}, so its records get record_timestamp. `,
      h('button', { class: 'link', type: 'button', textContent: 'Add rule', onclick: () => addTimestampRule(group) }));
  }

  const all = group === ALL;
  const searching = state.search.trim() !== '';
  let shown = 0;
  for (const g of all ? groups : [group])
    shown += renderGroupFields(view, g, all);

  if (!shown && searching)
    view.append(h('div', { class: 'empty', textContent: all ? 'Nothing matches the search.' : `Nothing in ${group} matches. Try "All groups".` }));
}

// The MES columns of a data group (the MES format, Config/mes-schema.json), or null for a group that is no MES table (MOTION, IO, ...).
const mesColumns = group => state.mesSchema?.[group] ?? null;

// Fields the group's TimeStamp rule fills (they need no incoming tag).
function ruleFilled(group) {
  const rule = state.data['timestamp-rules']?.[group];
  if (!rule) return new Set(['record_timestamp']);
  if (rule.mode === 'Span')
    return new Set([rule.startField, rule.endField, rule.previousField || (rule.triggerField && `previous_${rule.triggerField}`)].filter(Boolean));
  return new Set([rule.timestampField || 'record_timestamp']);
}

// One group: its MES fields (every MES column, just fill in the incoming tag),
// then extra fields (tag + own field name). Returns how many rows are shown.
function renderGroupFields(view, group, withTitle) {
  const columns = mesColumns(group);
  const searching = state.search.trim() !== '';
  const section = h('div', { class: 'field-section' });
  if (withTitle) section.append(h('h3', { class: 'sub', textContent: group }));
  let shown = 0;

  if (columns) {
    const filled = ruleFilled(group);
    const grid = h('div', { class: 'grid mes' },
      h('div', { class: 'head', textContent: 'MES field' }),
      h('div', { class: 'head', textContent: 'incoming tag (several: comma-separated)' }),
      h('div', { class: 'head' })); // a head too: hidden with the others in a narrow column, or every row shifts a cell
    for (const column of columns) {
      const rows = state.data['point-map'].filter(r => r.dataGroup === group && r.fieldName === column);
      const tags = rows.map(r => r.tag);
      if (!matches(`${column} ${tags.join(' ')} ${group}`)) continue;
      shown++;
      const byRule = filled.has(column);
      // Mapped automatically by name matching and not checked yet: ✓ confirms it (save to keep).
      const auto = rows.filter(r => r.auto != null);
      grid.append(
        h('div', { class: 'field-label', title: byRule ? 'The TimeStamp rule fills this field; a tag is optional.' : `MES column ${column}` },
          column, byRule ? h('span', { class: 'field-note', textContent: 'set by TimeStamp rule' }) : null,
          auto.length ? h('button', {
            type: 'button', class: 'auto-mark', textContent: `auto ${Math.max(...auto.map(r => r.auto)).toFixed(2)} ✓`,
            title: `Mapped automatically by name matching (${auto.map(r => `${r.tag}: ${r.auto}`).join(', ')}). Click to confirm it, then save.`,
            onclick: () => { auto.forEach(r => delete r.auto); markDirty('point-map'); render(); },
          }) : null),
        h('input', {
          value: tags.join(', '), 'aria-label': `incoming tag for ${column}`,
          placeholder: byRule ? 'optional' : 'no tag: sent as null',
          // on change (leaving the box), so typing a comma doesn't redraw the list
          onchange: e => setFieldTags(group, column, e.target.value),
        }),
        tags.length ? h('button', {
          class: 'row-x', type: 'button', textContent: '×',
          title: `Take ${tags.join(', ')} off ${column} on this tab: it becomes an extra field (not sent). ${column} itself stays.`,
          onclick: () => clearFieldTags(group, column, columns),
        }) : h('div'));
    }
    if (shown || !searching) section.append(grid);
    if (!searching)
      section.append(h('button', {
        type: 'button', class: 'link add-field', textContent: '+ MES field',
        title: `Add a column to the MES format of ${group} (all machines)`, onclick: () => addMesField(group),
      }));
  }

  // Extra fields: no MES column (never in the MES payload): "assign it" puts the
  // tag on a MES field. A non-MES group (MOTION, IO): every field, free text.
  const extraGrid = h('div', { class: 'grid two' },
    h('div', { class: 'head', textContent: 'incoming tag' }),
    h('div', { class: 'head', textContent: columns ? 'assign it' : 'fieldName' }),
    h('div', { class: 'head' }));
  let extras = 0;
  for (const row of state.data['point-map']) {
    if (row.dataGroup !== group || (columns ?? []).includes(row.fieldName)) continue;
    if (!matches(`${row.tag} ${row.fieldName} ${row.dataGroup}`)) continue;
    if (columns) appendExtraRow(extraGrid, row, group, columns);
    else appendPointRow(extraGrid, row, false, group);
    extras++;
  }
  if (!searching) {
    if (columns) appendExtraRow(extraGrid, null, group, columns);
    else for (let i = 0; i < BLANK_ROWS; i++) appendPointRow(extraGrid, null, false, group);
  }
  shown += extras;

  if (extras || !searching) {
    if (columns)
      section.append(h('p', { class: 'section-note extra-note', textContent:
        'Extra fields for this tab only: NOT sent to MES. Use "assign it" to put a tag on a MES field. ' +
        'A field that already has a tag gets both, combined with + (the gateway sends the values joined with _, e.g. 001_xxxx).' }));
    section.append(extraGrid);
  }

  if (shown || !searching) view.append(section);
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
  const filled = ruleFilled(group);

  const tag = h('input', {
    value: row?.tag ?? '', 'aria-label': 'incoming tag', placeholder: row ? '' : 'new tag',
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
      : filled.has(column) ? `${column}  (set by TimeStamp rule)` : column];
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

  const remove = h('button', {
    class: 'row-x', type: 'button', textContent: '×', title: 'Remove row',
    onclick: () => {
      if (!row) { tag.value = ''; return; }
      removeRow(row);
      markDirty('point-map');
      render();
    },
  });

  grid.append(tag, assign, remove);
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
    class: 'row-x', type: 'button', textContent: '×', title: 'Remove row',
    onclick: () => {
      if (!row) { tag.value = ''; field.value = ''; return; }
      removeRow(row);
      markDirty('point-map');
      render();
    },
  });

  grid.append(tag, field, remove);
}

// Tags the gateway received that are not in the point map yet.
function renderUnidentified(view, unid, groups) {
  view.append(h('p', {
    class: 'section-note',
    textContent: 'Tags the gateway received that are not in the point map (since it last started). Pick a group and a fieldName, then save to add them. ' +
      'A MES field that already has a tag gets both, combined with + (values joined with _).',
  }));

  const list = unid.filter(u => matches(`${u.tag} ${u.lastValue}`));
  if (!list.length) {
    view.append(h('div', { class: 'empty', textContent: unid.length ? 'No tags match the search.' : 'Nothing unidentified: every tag received so far is mapped.' }));
    return;
  }

  const grid = h('div', { class: 'grid unid' },
    ...['incoming tag', 'last value', 'group', 'field (MES column or extra)', 'name match'].map(text => h('div', { class: 'head', textContent: text })));

  for (const u of list) {
    const edit = state.unidEdits[u.tag] ??= { dataGroup: '', fieldName: '' };
    const field = h('input', {
      value: edit.fieldName, placeholder: u.tag.toLowerCase(), 'aria-label': 'fieldName',
      oninput: e => { edit.fieldName = e.target.value; markDirty('point-map'); },
    });
    field.setAttribute('list', `mes-${edit.dataGroup}`); // suggests the group's MES columns
    // Best MES column by name (gateway's NameMatcher); "use" fills group + field, save confirms.
    const s = u.suggestion;
    const suggestion = s ? h('div', { class: 'suggest-cell', title: s.conflict ? `Not mapped automatically: ${s.conflict}` : 'Below the auto-map score' },
      h('span', { textContent: `${s.group} / ${s.field} (${s.score.toFixed(2)})` }),
      s.conflict ? h('span', { class: 'field-note', textContent: s.conflict }) : null,
      h('button', { type: 'button', class: 'link', textContent: 'use', onclick: () => {
        edit.dataGroup = s.group; edit.fieldName = s.field; markDirty('point-map'); render();
      } })) : h('div', { class: 'value-cell', textContent: '—' });
    grid.append(
      h('input', { value: u.tag, readOnly: true, 'aria-label': 'incoming tag' }),
      h('div', { class: 'value-cell', textContent: `${u.lastValue}   ×${u.count}`, title: `last seen ${new Date(u.lastSeen).toLocaleString()}` }),
      select([['', '— pick —'], [IGNORED, 'ignore (never sent)'], ...groups.filter(g => g !== IGNORED && g !== 'UNIDENTIFIED').map(g => [g, g])], edit.dataGroup,
        v => { edit.dataGroup = v; field.setAttribute('list', `mes-${v}`); markDirty('point-map'); }, 'group'),
      field, suggestion);
  }

  view.append(grid,
    ...Object.entries(state.mesSchema ?? {}).map(([g, columns]) =>
      h('datalist', { id: `mes-${g}` }, ...columns.map(c => h('option', { value: c })))));
}

// TimeStamp: one rule per data group.
function renderTimestamp(view) {
  const rules = state.data['timestamp-rules'];
  const names = Object.keys(rules).filter(g => matches(`${g} ${JSON.stringify(rules[g])}`));

  let group = state.group['timestamp-rules'];
  if (!names.includes(group)) group = names[0] ?? '';
  state.group['timestamp-rules'] = group;

  fillPicker([
    ...(group ? [] : [['', '— none —']]),
    ...names.map(g => [g, g]),
    [NEW, '+ New group…'],
  ], group);

  if (!group) {
    view.append(h('div', {
      class: 'empty',
      textContent: Object.keys(rules).length ? 'No timestamp rule matches the search.' : 'No timestamp rules yet. Use "+ New group…" to add one.',
    }));
    return;
  }

  const rule = rules[group];
  rule.mode ??= 'SinglePoint';
  const span = rule.mode === 'Span';

  const form = h('div', { class: 'grid form' });
  form.append(
    h('div', { class: 'label', textContent: 'mode' }),
    select([['SinglePoint', 'SinglePoint'], ['Span', 'Span']], rule.mode,
      v => { rule.mode = v; markDirty('timestamp-rules'); render(); }, 'mode'));

  for (const key of span ? ['triggerField', 'startField', 'endField', 'previousField'] : ['timestampField']) {
    form.append(
      h('div', { class: 'label', textContent: key }),
      h('input', {
        value: rule[key] ?? '', 'aria-label': key,
        placeholder: key === 'timestampField' ? 'record_timestamp'
          : key === 'previousField' ? `previous_${rule.triggerField || '<triggerField>'} (default)` : '',
        oninput: e => { rule[key] = e.target.value; markDirty('timestamp-rules'); },
      }));
  }

  // How often this group's changes go to MES (applies live on save).
  form.append(
    h('div', { class: 'label', textContent: 'sendEveryMs' }),
    h('input', {
      type: 'number', min: '0', step: '1',
      value: rule.sendEveryMs ?? '', placeholder: '1000 (default)', 'aria-label': 'sendEveryMs',
      oninput: e => {
        if (e.target.value === '') delete rule.sendEveryMs;
        else rule.sendEveryMs = Number(e.target.value);
        markDirty('timestamp-rules');
      },
    }));

  view.append(form, h('p', {
    class: 'section-note',
    textContent: (span
      ? 'Span: when triggerField changes value, the running span is closed (endField) and a new one starts (startField); the old value goes to previousField.'
      : 'SinglePoint: timestampField is set to the time of each change (the machine\'s timestamp when sent).') +
      ' sendEveryMs: every change is sent to MES at the next tick, per machine; no change, nothing sent.',
  }), h('button', {
    type: 'button', textContent: 'remove rule',
    onclick: () => {
      if (!confirm(`Remove the timestamp rule for ${group}?`)) return;
      delete rules[group];
      markDirty('timestamp-rules');
      render();
    },
  }));
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
  password: { label: 'MQTT password', secret: true, help: 'Never shown: dots mean a password is saved. Type a new one to replace it, clear the field to remove it. Sent without encryption (plain MQTT).' },
  area: { label: 'Topic area', help: 'Topics the gateway makes itself start with svi/<area>/.' },
  keepaliveIntervalSeconds: { label: 'Keepalive every (s)', help: 'The gateway publishes {"alive":true} on svi/<area>/<gateway id>/keepalive.' },
  motionDeadband: { label: 'Motion: smallest change sent', help: 'A motion value is published again only after it moved at least this much.' },
  maxPublishRateMs: { label: 'IO / motion: ms between publishes', help: 'At most one publish per topic in this time (50 = 20 per second).' },
  // machine-config.json
  gatewayId: { label: 'Gateway id', help: 'Sent to MES in every record (gateway_id) and used in the keepalive topic.' },
  listenAddress: { label: 'Listen address', help: 'The IP of this PC that machines connect to. 0.0.0.0 = every address (needed in Docker).' },
  tcpPort: { label: 'Machine TCP port', help: 'Machines connect to listen address : this port.' },
  dataSource: { label: 'Default data source', help: 'Used for a message that has no data_source.' },
  timestampsWithoutZone: { label: 'A timestamp without a zone is', choices: [['UTC', 'UTC'], ['local', 'local time of this PC']] },
  protocolLabel: { label: 'Protocol label', help: 'Written into every MES record (protocol).' },
  mesUrl: { label: 'MES address (URL)', help: 'Where the records are sent, e.g. http://mes-server/api/v1/machine-events.' },
  unregisteredMachines: { label: 'Machine without a tab', choices: [['hold', 'hold its data until it gets a tab'], ['shared', 'map it with the Template']] },
  unmappedTags: { label: 'Tag not in the mapping', choices: [['send', 'send the known tags, leave it out'], ['hold', 'hold the whole message until it is mapped']] },
  outboxKeepSentDays: { label: 'Keep sent records (days)', help: 'A day file is deleted when it is this many days old and everything in it was sent.' },
  mesStuckRowTries: { label: 'MES: tries before a stuck record moves to failed', help: 'A record MES keeps answering 500 or 413 moves to failed messages after this many tries in a row, once MES took other records meanwhile. At least 1. Applied on Restart Gateway.' },
  nameMatchThreshold: { label: 'Name matching: auto-map from score', help: 'An unknown tag whose name matches one MES column at least this well (0.5 to 1; 1 = the same name) is mapped automatically and marked "auto" in PointMap. Lower scores, or a column in several groups or already filled, are only suggested (Unidentified). Applied on Restart Gateway.' },
  nameMatchSynonyms: { label: 'Name matching: words that mean the same', help: 'short=long, comma-separated, e.g. qty=quantity, sn=serial number. Used to compare tag names with MES columns. Applied on Restart Gateway.' },
  mesStuckRowMinutes: { label: 'MES: minutes before a stuck record moves to failed (when nothing else is sent)', help: 'Same, when MES took nothing else meanwhile (e.g. one machine alone). 1 to 10. Applied on Restart Gateway.' },
};

// MQTTConfig / MachineConfig: one setting per row.
function renderForm(view) {
  const key = state.view;
  const data = state.data[key];
  const form = h('div', { class: 'grid form' });
  let shown = 0;

  for (const [name, value] of Object.entries(data)) {
    const info = SETTINGS[name] ?? {};
    if (!matches(`${name} ${info.label ?? ''} ${typeof value === 'object' ? JSON.stringify(value) : value}`)) continue;
    shown++;
    form.append(h('div', { class: 'label' }, info.label ?? name, info.label ? h('span', { class: 'key', textContent: name }) : null));

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
      input = select([['true', 'true'], ['false', 'false']], String(value), v => { data[name] = v === 'true'; markDirty(key); }, name);
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
    form.append(info.help ? h('div', {}, input, h('div', { class: 'help', textContent: info.help })) : input);
  }

  if (key === 'mqtt-config') view.append(h('div', { class: 'mqtt-status', id: 'mqttState' }));
  view.append(shown ? form : h('div', { class: 'empty', textContent: 'No settings match the search.' }));
  showMqttState();
}

// MQTTConfig: the broker connection right now (from the gateway, not from the log).
function showMqttState() {
  const el = $('mqttState');
  const connected = state.status?.mqttConnected;
  if (!el) return;
  el.replaceChildren(h('span', { class: `dot ${connected ? 'connected' : 'disconnected bad'}` }),
    connected == null ? ' MQTT: unknown (gateway not running)' : connected ? ' MQTT: connected' : ' MQTT: disconnected');
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
    if (state.view === 'timestamp-rules' && !state.data['timestamp-rules'][name]) {
      state.data['timestamp-rules'][name] = { mode: 'SinglePoint', timestampField: 'record_timestamp' };
      markDirty('timestamp-rules');
    }
    state.group[state.view] = name;
  }

  render(); // also puts the picker back if the prompt was cancelled
}

function addTimestampRule(group) {
  state.data['timestamp-rules'][group] ??= { mode: 'SinglePoint', timestampField: 'record_timestamp' };
  markDirty('timestamp-rules');
  state.group['timestamp-rules'] = group;
  state.search = '';
  $('search').value = '';
  switchView('timestamp-rules');
  toast(`Rule added for ${group}. Check it and press save.`);
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
  const key = state.view;

  if (key === 'point-map' && state.group['point-map'] === UNID && !Object.values(state.unidEdits).some(e => e.dataGroup)) {
    toast('Pick a group for at least one unidentified tag first.', true);
    return;
  }

  let body;
  try { body = buildBody(key); }
  catch (e) { toast(e.message, true); return; }

  // The MES format (shared by all machines) was edited on PointMap: save it first.
  if (key === 'point-map' && state.mesDirty) {
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

  // On a machine tab: this machine only, similar machines, or all (only what changed is copied).
  const changes = MAPPING.has(key) && state.scope && state.saved[key] ? mappingChanges(key, state.saved[key], body) : null;
  const targets = changes?.count ? await chooseSaveScope(changes.count) : [];
  if (targets === null) return; // cancelled

  let result;
  try {
    result = await request(`/api/config/${key}${scopeQuery(key)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  } catch (e) {
    toast(`Not saved: ${e.message}`, true);
    return;
  }

  state.data[key] = body;
  if (MAPPING.has(key)) state.saved[key] = structuredClone(body);
  if (targets.length) await applyToOthers(key, changes, targets);
  state.dirty.delete(key);
  if (key === 'point-map') {
    state.unidEdits = {};
    state.extraGroups = state.extraGroups.filter(g => !body.some(r => r.dataGroup === g));
    await loadUnidentified();
  }
  render();
  if (result.reconnected > 0) {
    toast(`Every held tag is mapped now: ${result.reconnected} connection(s) closed, the held messages are being sent again.`);
    await loadUnmapped();
  }
  askRestart(result.restartToApply);
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
        h('button', { type: 'button', class: 'primary', textContent: `Just ${me}`, onclick: () => done([]) }),
        h('button', { type: 'button', textContent: `Similar machines (${similar.length})`, disabled: !similar.length, onclick: () => done(similar) }),
        h('button', { type: 'button', textContent: `All machines (${others.length}) + Template`, onclick: () => done([...others, '']) })),
      h('p', { class: 'dlg-small', textContent: similar.length ? `Similar (name starts like ${me}): ${names(similar)}` : 'No other machine has a similar name.' }),
      h('p', { class: 'dlg-small', textContent: 'Only what you changed here is copied; the other machines keep the rest of their settings.' }),
      h('div', { class: 'dlg-buttons' }, h('button', { type: 'button', textContent: 'Cancel', onclick: () => done(null) })));
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
      const data = await request(`/api/config/${key}${query}`);
      await request(`/api/config/${key}${query}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(applyChanges(key, data, changes)),
      });
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

// ---------- gateway status (bottom of the left panel) ----------

function showStatus(status) {
  state.status = status;
  // The MQTT banner follows the real connection (mqttConnected), not log lines.
  state.problems.mqtt = status.mqttConnected === false ? (state.problems.mqtt ?? new Date().toISOString()) : null;
  showMqttState();
  $('gwStatus').className = `gw-status ${status.state}`;
  const since = status.startedAt ? new Date(status.startedAt).toLocaleTimeString() : '';
  $('gwText').textContent =
    status.state === 'running' ? `Gateway running · TCP ${status.tcpPort} · since ${since}` :
    status.state === 'error' ? `Gateway stopped: ${status.lastError} (it tries again by itself)` :
    `Gateway ${status.state}…`;

  // Last 10 s, per second: lines in, MQTT out, MES delivered, rows waiting for MES.
  const s = status.stats;
  const n = v => Math.round(v).toLocaleString();
  const rate = v => v < 10 ? v.toFixed(1) : n(v); // 1 message in 10 s shows as 0.1, not 0
  $('gwStats').textContent = s && s.at
    ? `in ${rate(s.inPerSec)}/s · MQTT ${rate(s.mqttPerSec)}/s · MES ${rate(s.mesDeliveredPerSec)}/s · backlog ${s.backlog < 0 ? '?' : n(s.backlog)}` +
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
  const option = $('picker').querySelector(`option[value="${UNID}"]`);
  if (option) option.textContent = `⚠ Unidentified (${unidentifiedList().length})`;
}

// ---------- wiring ----------

document.querySelectorAll('.nav').forEach(button =>
  button.addEventListener('click', () => switchView(button.dataset.view)));

// ---------- column widths: drag a grip line (or ← / →), double-click for the default ----------
// Remembered in this browser only (a per-viewer convenience; the page works without it).
const COLUMN_DEFAULTS = { tabs: 210, side: 210, general: 230 };
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
  const sign = col === 'general' ? -1 : 1; // General sits right of its grip: dragging left widens it
  grip.addEventListener('pointerdown', e => {
    e.preventDefault();
    grip.setPointerCapture(e.pointerId);
    const startX = e.clientX, startWidth = columnWidth(col);
    grip.classList.add('dragging');
    document.body.classList.add('resizing');
    const move = ev => setColumn(col, startWidth + sign * (ev.clientX - startX));
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
    setColumn(col, columnWidth(col) + (e.key === 'ArrowRight' ? 16 : -16) * sign);
    saveColumns();
  });
});

$('picker').addEventListener('change', async e => {
  const value = e.target.value;
  if (value === NEW) { addNewGroup(); return; }
  state.group[state.view] = value;
  if (value === UNID) await loadUnidentified();
  render();
});

$('search').addEventListener('input', e => { state.search = e.target.value; render(); });
$('search').addEventListener('keydown', e => { if (e.key === 'Enter') render(); });
$('searchBtn').addEventListener('click', () => { state.search = $('search').value; render(); });

$('saveBtn').addEventListener('click', save);
$('errBadge').addEventListener('click', () => switchView('log'));
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
