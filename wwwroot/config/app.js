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

const state = {
  view: 'point-map',
  data: {},                 // file key -> data as loaded / being edited
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
  logsLoaded: false,        // no pop-ups for the backlog on first load
  errorsOnly: false,
};
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
  const res = await fetch(url, { cache: 'no-store', ...options });
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
  for (const key of Object.keys(FILES)) {
    const data = await request(`/api/config/${key}`);
    state.data[key] = data;
    if (!Array.isArray(data))
      state.numberKeys[key] = Object.entries(data).filter(([, v]) => typeof v === 'number').map(([k]) => k);
  }
  state.dirty.clear();
  await loadUnidentified();
}

async function loadUnidentified() {
  try { state.unidentified = await request('/api/unidentified'); }
  catch { state.unidentified = []; }
}

function pointMapGroups() {
  const groups = [];
  const add = g => { if (g && !groups.includes(g)) groups.push(g); };
  state.data['point-map'].forEach(row => add(row.dataGroup));
  Object.keys(state.data['timestamp-rules'] ?? {}).forEach(add);
  state.extraGroups.forEach(add);
  return groups;
}

function unidentifiedList() {
  const mapped = new Set(state.data['point-map'].map(row => (row.tag ?? '').toLowerCase()));
  return state.unidentified.filter(u => !mapped.has(u.tag.toLowerCase()));
}

// ---------- rendering ----------

function updateNav() {
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
  $('saveBtn').hidden = isLog;

  if (state.view === 'point-map') renderPointMap(view);
  else if (state.view === 'timestamp-rules') renderTimestamp(view);
  else if (isLog) renderLog(view);
  else renderForm(view);

  $('note').textContent =
    isLog ? 'Newest at the bottom. Full history: logs\\gateway-<date>.log next to the exe (14 days).' :
    FILES[state.view].restartToApply ? 'Applies after the gateway restarts.' :
    'Applies as soon as you save.';
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

  // Pop up the newest new problem (only for lines that arrive while the page is open).
  const newest = fresh.filter(l => l.isError).pop();
  if (state.logsLoaded && newest && state.view !== 'log') toast(`Gateway: ${newest.text}`, true);
  state.logsLoaded = true;

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
  const grid = h('div', { class: all ? 'grid three' : 'grid two' });
  if (all) grid.append(h('div', { class: 'head', textContent: 'group' }));
  grid.append(
    h('div', { class: 'head', textContent: 'incoming tag' }),
    h('div', { class: 'head', textContent: 'fieldName' }),
    h('div'));

  let shown = 0;
  for (const row of state.data['point-map']) {
    if (!all && row.dataGroup !== group) continue;
    if (!matches(`${row.tag} ${row.fieldName} ${row.dataGroup}`)) continue;
    appendPointRow(grid, row, all, group);
    shown++;
  }

  if (!all && !searching)
    for (let i = 0; i < BLANK_ROWS; i++) appendPointRow(grid, null, false, group);

  view.append(grid);

  if (!shown && searching)
    view.append(h('div', { class: 'empty', textContent: all ? 'No rows match the search.' : `No rows in ${group} match. Try "All groups".` }));
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
      rows.splice(rows.indexOf(row), 1);
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
    textContent: 'Tags the gateway received that are not in the point map (since it last started). Pick a group and a fieldName, then save to add them.',
  }));

  const list = unid.filter(u => matches(`${u.tag} ${u.lastValue}`));
  if (!list.length) {
    view.append(h('div', { class: 'empty', textContent: unid.length ? 'No tags match the search.' : 'Nothing unidentified: every tag received so far is mapped.' }));
    return;
  }

  const grid = h('div', { class: 'grid unid' },
    ...['incoming tag', 'last value', 'group', 'fieldName'].map(text => h('div', { class: 'head', textContent: text })));

  for (const u of list) {
    const edit = state.unidEdits[u.tag] ??= { dataGroup: '', fieldName: '' };
    grid.append(
      h('input', { value: u.tag, readOnly: true, 'aria-label': 'incoming tag' }),
      h('div', { class: 'value-cell', textContent: `${u.lastValue}   ×${u.count}`, title: `last seen ${new Date(u.lastSeen).toLocaleString()}` }),
      select([['', '— pick —'], ...groups.map(g => [g, g])], edit.dataGroup,
        v => { edit.dataGroup = v; markDirty('point-map'); }, 'group'),
      h('input', {
        value: edit.fieldName, placeholder: u.tag.toLowerCase(), 'aria-label': 'fieldName',
        oninput: e => { edit.fieldName = e.target.value; markDirty('point-map'); },
      }));
  }

  view.append(grid);
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

  for (const key of span ? ['triggerField', 'startField', 'endField'] : ['timestampField']) {
    form.append(
      h('div', { class: 'label', textContent: key }),
      h('input', {
        value: rule[key] ?? '', 'aria-label': key,
        placeholder: key === 'timestampField' ? 'record_timestamp' : '',
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
      ? 'Span: when triggerField changes value, the running span is closed (endField) and a new one starts (startField).'
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

// MQTTConfig / MachineConfig: one setting per row.
function renderForm(view) {
  const key = state.view;
  const data = state.data[key];
  const form = h('div', { class: 'grid form' });
  let shown = 0;

  for (const [name, value] of Object.entries(data)) {
    if (!matches(`${name} ${typeof value === 'object' ? JSON.stringify(value) : value}`)) continue;
    shown++;
    form.append(h('div', { class: 'label', textContent: name }));

    if (value !== null && typeof value === 'object') {
      form.append(h('input', { value: JSON.stringify(value), readOnly: true, title: 'Not editable here; edit the file itself.' }));
    } else if (typeof value === 'boolean') {
      form.append(select([['true', 'true'], ['false', 'false']], String(value), v => { data[name] = v === 'true'; markDirty(key); }, name));
    } else {
      const isNumber = state.numberKeys[key]?.includes(name);
      form.append(h('input', {
        ...(isNumber ? { type: 'number', step: 'any' } : {}),
        value: value ?? '', 'aria-label': name,
        oninput: e => {
          const text = e.target.value;
          data[name] = isNumber && text !== '' ? Number(text) : text;
          markDirty(key);
        },
      }));
    }
  }

  view.append(shown ? form : h('div', { class: 'empty', textContent: 'No settings match the search.' }));
}

// ---------- actions ----------

function switchView(view) {
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
      .map(r => ({ tag: (r.tag ?? '').trim(), dataGroup: r.dataGroup, fieldName: (r.fieldName ?? '').trim() }))
      .filter(r => r.tag || r.fieldName);

    for (const [tag, edit] of Object.entries(state.unidEdits)) {
      const fieldName = edit.fieldName.trim();
      if (!edit.dataGroup && fieldName) throw new Error(`Pick a group for the unidentified tag '${tag}'.`);
      if (edit.dataGroup) rows.push({ tag, dataGroup: edit.dataGroup, fieldName: fieldName || tag.toLowerCase() });
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
      const keep = ['mode', ...fields, 'sendEveryMs'];
      for (const k of Object.keys(rule)) if (!keep.includes(k)) delete rule[k];
      for (const k of keep) if (typeof rule[k] === 'string') rule[k] = rule[k].trim();

      if ('sendEveryMs' in rule && !(Number.isInteger(rule.sendEveryMs) && rule.sendEveryMs >= 0 && rule.sendEveryMs <= 3600000))
        throw new Error(`${group}: sendEveryMs must be a whole number from 0 to 3600000 (empty = 1000).`);

      if (rule.mode === 'Span' && fields.some(k => !rule[k]))
        throw new Error(`${group}: Span needs triggerField, startField and endField.`);
      if (rule.mode !== 'Span' && !rule.timestampField) delete rule.timestampField; // falls back to record_timestamp
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

  let result;
  try {
    result = await request(`/api/config/${key}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  } catch (e) {
    toast(`Not saved: ${e.message}`, true);
    return;
  }

  state.data[key] = body;
  state.dirty.delete(key);
  if (key === 'point-map') {
    state.unidEdits = {};
    state.extraGroups = state.extraGroups.filter(g => !body.some(r => r.dataGroup === g));
    await loadUnidentified();
  }
  render();
  askRestart(result.restartToApply);
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
  $('gwStatus').className = `gw-status ${status.state}`;
  const since = status.startedAt ? new Date(status.startedAt).toLocaleTimeString() : '';
  $('gwText').textContent =
    status.state === 'running' ? `Gateway running · TCP ${status.tcpPort} · since ${since}` :
    status.state === 'error' ? `Gateway stopped: ${status.lastError}` :
    `Gateway ${status.state}…`;

  // Last 10 s, per second: lines in, MQTT out, MES delivered, rows waiting for MES.
  const s = status.stats;
  const n = v => Math.round(v).toLocaleString();
  const rate = v => v < 10 ? v.toFixed(1) : n(v); // 1 message in 10 s shows as 0.1, not 0
  $('gwStats').textContent = s && s.at
    ? `in ${rate(s.inPerSec)}/s · MQTT ${rate(s.mqttPerSec)}/s · MES ${rate(s.mesDeliveredPerSec)}/s · backlog ${s.backlog < 0 ? '?' : n(s.backlog)}` +
      (s.rejectedPerSec > 0 ? ` · ${rate(s.rejectedPerSec)}/s without machine_id` : '') +
      (s.mqttLostPerSec > 0 ? ` · MQTT lost ${rate(s.mqttLostPerSec)}/s` : '')
    : '';
}

async function pollStatus() {
  try {
    showStatus(await request('/api/status'));
  } catch {
    $('gwStatus').className = 'gw-status error';
    $('gwText').textContent = 'Cannot reach the gateway.';
    return;
  }

  // Keep the Unidentified count fresh without redrawing what the user is typing in.
  await loadUnidentified();
  const option = $('picker').querySelector(`option[value="${UNID}"]`);
  if (option) option.textContent = `⚠ Unidentified (${unidentifiedList().length})`;
}

// ---------- wiring ----------

document.querySelectorAll('.nav').forEach(button =>
  button.addEventListener('click', () => switchView(button.dataset.view)));

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
  setInterval(() => { pollStatus(); pollLogs(); }, 4000);
})();
