import { McpClient } from './mcp.js';
import { escapeHtml, shortType, splitEntityKey, formatMs, formatTime, formatBytes, renderJsonTree } from './json-view.js';
import { TimelineView } from './timeline.js';
import { GraphView } from './graph.js';

const mcpMeta = document.querySelector('meta[name="sharedmeta-mcp-path"]');
const defaultMcpPath = mcpMeta?.getAttribute('content') || '/mcp';

const mcp = new McpClient(defaultMcpPath);

const state = {
  view: 'timeline',
  status: null,
  capture: null,
  schema: null,
  entities: [],
  sessions: [],
  streamEvents: [],
  selectedStreamEvent: null,
  timelineMeta: null,
  graphData: null,
  selectedMethod: null,
  invokeMode: 'form', // 'form' | 'json'
  livePolling: false,
  pollTimer: null,
  sidebarFilter: '',
  methodSearch: '',
  captureDraft: null,
  captureDirty: false,
};

const $ = (id) => document.getElementById(id);

// ── Toast & Modals ───────────────────────────────────────────────────────────

function toast(message, kind = 'info') {
  const stack = $('toast-stack');
  const el = document.createElement('div');
  el.className = `toast ${kind === 'ok' ? 'toast-ok' : kind === 'error' ? 'toast-error' : ''}`;
  el.textContent = message;
  stack.appendChild(el);
  setTimeout(() => el.remove(), 4200);
}

function openModal(id) {
  const el = $(id);
  if (el) el.hidden = false;
}

function closeModals() {
  document.querySelectorAll('.modal-backdrop').forEach(m => { m.hidden = true; });
}

document.querySelectorAll('[data-close-modal]').forEach(btn => btn.addEventListener('click', closeModals));
document.querySelectorAll('.modal-backdrop').forEach(bg => {
  bg.addEventListener('click', (e) => { if (e.target === bg) closeModals(); });
});
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModals(); });

mcp.onAuthRequired = (msg) => {
  $('endpoint-auth-note').textContent = msg || 'This inspector requires an API key.';
  $('inp-mcp-url').value = mcp.url;
  $('inp-api-key').value = mcp.apiKey;
  openModal('modal-endpoint');
};

// ── Components ───────────────────────────────────────────────────────────────

const timelineView = new TimelineView({
  canvasEl: $('tl-canvas'),
  cardEl: $('tl-card'),
  scrubberEl: $('tl-scrubber'),
  callbacks: {
    onOpenCallTree: (callId) => openCallTree(callId),
    onOpenEntity: (stateType, entityId) => openEntityTimeline(stateType, entityId),
    fetchStateAt: (stateType, entityId, seq) => mcp.entityStateAt(stateType, entityId, seq),
  },
});

const graphView = new GraphView({
  canvasEl: $('g-canvas'),
  detailEl: $('g-detail'),
  callbacks: {
    onOpenTimeline: (stateType, entityId) => openEntityTimeline(stateType, entityId),
    onRecenter: (stateType, entityId) => openEntityGraph(stateType, entityId),
    onWatchEntity: async (entityId) => { await quickWatchEntity(entityId, 'Full'); },
    onWatchPlayer: async (playerId) => { await quickWatchPlayer(playerId, 'ArgsResult'); },
    onOpenLiveState: (stateType, entityId) => openLiveState(stateType, entityId),
    onFilterPlayer: (playerId) => {
      $('st-player').value = playerId;
      switchView('stream');
      loadStream();
    },
  },
});

// ── Navigation ───────────────────────────────────────────────────────────────

function switchView(viewName) {
  state.view = viewName;
  document.querySelectorAll('.nav-tab').forEach(btn => {
    btn.classList.toggle('is-active', btn.dataset.view === viewName);
  });
  document.querySelectorAll('.view-pane').forEach(pane => {
    pane.classList.toggle('is-active', pane.id === `view-${viewName}`);
  });
  if (viewName === 'stream' && state.streamEvents.length === 0) loadStream();
  if (viewName === 'entities') { loadEntities(); loadSessions(); }
  if (viewName === 'methods' && !state.schema) loadSchema();
  if (viewName === 'capture') renderCaptureView();
  if (viewName === 'graph' && state.graphData) graphView.render();
}

document.querySelectorAll('.nav-tab').forEach(btn => {
  btn.addEventListener('click', () => switchView(btn.dataset.view));
});

$('btn-capture-pill').addEventListener('click', () => switchView('capture'));

$('btn-endpoint').addEventListener('click', () => {
  $('inp-mcp-url').value = mcp.url;
  $('inp-api-key').value = mcp.apiKey;
  $('endpoint-auth-note').textContent = '';
  openModal('modal-endpoint');
});

$('btn-save-endpoint').addEventListener('click', async () => {
  mcp.setEndpoint($('inp-mcp-url').value);
  mcp.setApiKey($('inp-api-key').value);
  closeModals();
  await bootstrapAll();
});

$('btn-refresh').addEventListener('click', () => refreshActiveView());

$('btn-live-toggle').addEventListener('click', () => {
  state.livePolling = !state.livePolling;
  $('btn-live-toggle').classList.toggle('is-live', state.livePolling);
  schedulePolling();
});

$('sel-poll-interval').addEventListener('change', () => {
  if (state.livePolling) schedulePolling();
});

function schedulePolling() {
  if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
  if (!state.livePolling) return;
  const ms = Number($('sel-poll-interval').value) || 3000;
  state.pollTimer = setInterval(() => refreshActiveView(true), ms);
}

// ── Core Data Loading ────────────────────────────────────────────────────────

async function bootstrapAll() {
  try {
    const [status, capture, schema, entities, sessions] = await Promise.all([
      mcp.status(),
      mcp.getCaptureConfig(),
      mcp.listMethods(),
      mcp.listEntities(null, 200),
      mcp.listSessions(),
    ]);
    state.status = status;
    state.capture = capture;
    syncCaptureDraft(capture);
    state.schema = schema;
    state.entities = entities?.entities || [];
    state.sessions = Array.isArray(sessions) ? sessions : [];

    updateStatusUi();
    updateDatalists();
    renderSidebar();
    renderMethodsCatalog();
    renderCaptureView();
    renderEntitiesTables();

    // Auto-select most recent entity for Timeline if none selected yet
    if (!$('tl-entity-id').value && state.entities.length > 0) {
      const first = state.entities[0];
      $('tl-state-type').value = shortType(first.stateType);
      $('tl-entity-id').value = first.entityId;
      await loadTimeline();
    } else {
      timelineView.render();
    }
  } catch (err) {
    setConnectionError(err.message);
    if (!err.isAuthError) toast(err.message, 'error');
  }
}

async function refreshActiveView(silent = false) {
  try {
    const [status, capture, entities] = await Promise.all([
      mcp.status(),
      mcp.getCaptureConfig(),
      mcp.listEntities(null, 200),
    ]);
    state.status = status;
    state.capture = capture;
    state.entities = entities?.entities || [];
    updateStatusUi();
    updateDatalists();
    renderSidebar();

    if (state.view === 'timeline' && $('tl-state-type').value && $('tl-entity-id').value) {
      await loadTimeline(silent);
    } else if (state.view === 'graph' && $('g-state-type').value && $('g-entity-id').value) {
      await loadGraph(silent);
    } else if (state.view === 'stream') {
      await loadStream(silent);
    } else if (state.view === 'entities') {
      await loadSessions();
      renderEntitiesTables();
    } else if (state.view === 'capture') {
      if (!silent || !state.captureDirty) syncCaptureDraft(capture);
      renderCaptureView({ updateForm: !silent || !state.captureDirty });
    }
  } catch (err) {
    setConnectionError(err.message);
    if (!silent && !err.isAuthError) toast(err.message, 'error');
  }
}

function setConnectionError(msg) {
  $('conn-dot').className = 'dot is-error';
  $('status-silo').textContent = 'Disconnected';
  $('capture-pill-text').textContent = 'ERR';
  $('btn-capture-pill').title = msg;
}

function updateStatusUi() {
  const s = state.status;
  if (!s) return;
  const capturing = s.level !== 'Off' || (s.watchedEntities > 0) || (s.watchedPlayers > 0);
  $('conn-dot').className = capturing ? 'dot is-active' : 'dot is-online';
  const distShort = s.sharedCaptureSettings ? 'NATS' : 'local';
  $('status-silo').textContent = `${s.silo || 'inspector'} · ${distShort}${s.liveAccess ? '' : ' (standalone)'}`;

  const watchCount = (s.watchedEntities || 0) + (s.watchedPlayers || 0);
  $('capture-pill-text').textContent = watchCount > 0
    ? `${s.level} · ${watchCount} watch${watchCount === 1 ? '' : 'es'}`
    : String(s.level || 'Off').toUpperCase();

  $('m-stored').textContent = s.storedEvents ?? 0;
  $('m-sessions').textContent = s.openSessions ?? 0;
  $('m-dropped').textContent = s.droppedEvents ?? 0;

  const invBadge = $('invoke-allowed-badge');
  if (invBadge) {
    invBadge.textContent = s.invokeAllowed ? 'invoke enabled' : 'invoke disabled';
    invBadge.className = `badge ${s.invokeAllowed ? 'badge-ok' : 'badge-neutral'}`;
  }
  updateTimelineWatchButton();
}

function updateDatalists() {
  const stTypes = new Set();
  for (const t of state.schema?.stateTypes || []) {
    stTypes.add(shortType(t));
    stTypes.add(t);
  }
  for (const e of state.entities) {
    if (e.stateType) stTypes.add(shortType(e.stateType));
  }
  $('datalist-state-types').innerHTML = [...stTypes].sort().map(t => `<option value="${escapeHtml(t)}"></option>`).join('');

  const entIds = new Set(state.entities.map(e => e.entityId).filter(Boolean));
  for (const id of Object.keys(state.capture?.watchedEntities || {})) entIds.add(id);
  $('datalist-entity-ids').innerHTML = [...entIds].map(id => `<option value="${escapeHtml(id)}"></option>`).join('');

  const meths = new Set();
  for (const m of state.schema?.methods || []) {
    meths.add(`${m.service}.${m.method}`);
    meths.add(`${m.service}.*`);
  }
  $('datalist-methods').innerHTML = [...meths].sort().map(m => `<option value="${escapeHtml(m)}"></option>`).join('');
}

// ── Sidebar ──────────────────────────────────────────────────────────────────

$('sidebar-entity-filter').addEventListener('input', (e) => {
  state.sidebarFilter = e.target.value.trim().toLowerCase();
  renderSidebar();
});

$('btn-quick-watch').addEventListener('click', () => switchView('capture'));

function renderSidebar() {
  // Watches
  const watchEl = $('sidebar-watches');
  const wEnts = Object.entries(state.capture?.watchedEntities || {});
  const wPlayers = Object.entries(state.capture?.watchedPlayers || {});

  if (wEnts.length === 0 && wPlayers.length === 0) {
    watchEl.innerHTML = `<div class="muted-note">No active watches. Click + to add.</div>`;
  } else {
    watchEl.innerHTML = [
      ...wEnts.map(([id, lvl]) => `
        <div class="side-item" data-watch-entity="${escapeHtml(id)}">
          <div class="side-item-main">
            <div class="side-item-title">${escapeHtml(id)}</div>
            <div class="side-item-sub">entity · ${escapeHtml(lvl)}</div>
          </div>
          <button type="button" class="btn-icon" data-unwatch-entity="${escapeHtml(id)}" title="Unwatch">✕</button>
        </div>
      `),
      ...wPlayers.map(([id, lvl]) => `
        <div class="side-item" data-watch-player="${escapeHtml(id)}">
          <div class="side-item-main">
            <div class="side-item-title">player:${escapeHtml(id)}</div>
            <div class="side-item-sub">player · ${escapeHtml(lvl)}</div>
          </div>
          <button type="button" class="btn-icon" data-unwatch-player="${escapeHtml(id)}" title="Unwatch">✕</button>
        </div>
      `),
    ].join('');

    watchEl.querySelectorAll('[data-unwatch-entity]').forEach(btn => {
      btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        await unwatchEntity(btn.getAttribute('data-unwatch-entity'));
      });
    });
    watchEl.querySelectorAll('[data-unwatch-player]').forEach(btn => {
      btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        await unwatchPlayer(btn.getAttribute('data-unwatch-player'));
      });
    });
    watchEl.querySelectorAll('[data-watch-entity]').forEach(row => {
      row.addEventListener('click', () => {
        const id = row.getAttribute('data-watch-entity');
        const seen = state.entities.find(e => e.entityId === id);
        if (seen) openEntityTimeline(shortType(seen.stateType), seen.entityId);
        else {
          $('tl-entity-id').value = id;
          switchView('timeline');
        }
      });
    });
    watchEl.querySelectorAll('[data-watch-player]').forEach(row => {
      row.addEventListener('click', () => {
        $('st-player').value = row.getAttribute('data-watch-player');
        switchView('stream');
        loadStream();
      });
    });
  }

  // Seen Entities
  const entEl = $('sidebar-entities');
  const filtered = state.entities.filter(e => {
    if (!state.sidebarFilter) return true;
    return `${e.stateType}:${e.entityId}`.toLowerCase().includes(state.sidebarFilter);
  });
  $('sidebar-entity-count').textContent = filtered.length;

  const activeKey = timelineView.entityKey;
  if (filtered.length === 0) {
    entEl.innerHTML = `<div class="muted-note">No entities seen yet.</div>`;
    return;
  }

  entEl.innerHTML = filtered.map(e => {
    const watched = state.capture?.watchedEntities?.[e.entityId];
    const selected = activeKey === e.entityKey;
    return `
      <button type="button" class="side-item ${selected ? 'is-selected' : ''}" data-st="${escapeHtml(e.stateType)}" data-id="${escapeHtml(e.entityId)}">
        <div class="side-item-main">
          <div class="side-item-title">${escapeHtml(e.entityId)}</div>
          <div class="side-item-sub">${escapeHtml(shortType(e.stateType))} · #${e.lastSequence}</div>
        </div>
        ${watched ? `<span class="badge badge-amber">${escapeHtml(watched)}</span>` : ''}
      </button>
    `;
  }).join('');

  entEl.querySelectorAll('.side-item').forEach(btn => {
    btn.addEventListener('click', () => {
      openEntityTimeline(shortType(btn.dataset.st), btn.dataset.id);
    });
  });
}

// ── View 1: Timeline ─────────────────────────────────────────────────────────

function openEntityTimeline(stateType, entityId) {
  $('tl-state-type').value = shortType(stateType);
  $('tl-entity-id').value = entityId;
  switchView('timeline');
  loadTimeline();
}

async function loadTimeline(silent = false) {
  const stateType = $('tl-state-type').value.trim();
  const entityId = $('tl-entity-id').value.trim();
  if (!stateType || !entityId) {
    if (!silent) toast('Enter both StateType and Entity ID to load a timeline.', 'error');
    return;
  }

  const fromSeqRaw = $('tl-from-seq').value.trim();
  const toSeqRaw = $('tl-to-seq').value.trim();
  const limit = Number($('tl-limit').value) || 200;

  try {
    const res = await mcp.entityTimeline({
      stateType,
      entityId,
      fromSeq: fromSeqRaw !== '' ? Number(fromSeqRaw) : undefined,
      toSeq: toSeqRaw !== '' ? Number(toSeqRaw) : undefined,
      limit,
      includePayloads: true,
    });
    state.timelineMeta = res;
    timelineView.setData(res.entity || `${stateType}:${entityId}`, res.events || []);
    updateTimelineWatchButton();
    renderSidebar();

    const strip = $('tl-info-strip');
    const hist = res.history;
    strip.hidden = false;
    strip.innerHTML = `
      <span>Entity: <code>${escapeHtml(res.entity)}</code></span>
      <span>Events: <strong>${res.count ?? 0}</strong></span>
      ${hist ? `<span>History: <strong>${hist.read ?? 0}</strong> read (${hist.olderThanStore ?? 0} older${hist.truncated ? ', truncated' : ''})</span>` : ''}
      ${hist?.error ? `<span class="badge badge-error">History error: ${escapeHtml(hist.error)}</span>` : ''}
    `;
  } catch (err) {
    if (!silent) toast(err.message, 'error');
  }
}

function updateTimelineWatchButton() {
  const entityId = $('tl-entity-id').value.trim();
  const btn = $('tl-watch-btn');
  if (!btn) return;
  const level = entityId ? state.capture?.watchedEntities?.[entityId] : null;
  if (level) {
    btn.textContent = `★ Watching (${level})`;
    btn.className = 'btn btn-sm btn-cyan';
  } else {
    btn.textContent = 'Watch Full';
    btn.className = 'btn btn-sm';
  }
}

$('tl-load-btn').addEventListener('click', () => loadTimeline());
$('tl-entity-id').addEventListener('change', () => updateTimelineWatchButton());
$('tl-zoom').addEventListener('change', (e) => timelineView.setZoom(e.target.value));

$('tl-watch-btn').addEventListener('click', async () => {
  const entityId = $('tl-entity-id').value.trim();
  if (!entityId) { toast('Enter an Entity ID first.', 'error'); return; }
  if (state.capture?.watchedEntities?.[entityId]) {
    await unwatchEntity(entityId);
  } else {
    await quickWatchEntity(entityId, 'Full');
  }
});

$('tl-mermaid-btn').addEventListener('click', () => {
  if (!state.timelineMeta?.mermaid) { toast('Load a timeline first.', 'error'); return; }
  $('mermaid-title').textContent = `Timeline Mermaid — ${shortType(state.timelineMeta.entity)}`;
  $('mermaid-code').textContent = state.timelineMeta.mermaid;
  openModal('modal-mermaid');
});

$('tl-graph-btn').addEventListener('click', () => {
  const st = $('tl-state-type').value.trim();
  const id = $('tl-entity-id').value.trim();
  if (st && id) openEntityGraph(st, id);
  else switchView('graph');
});

$('btn-copy-mermaid').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText($('mermaid-code').textContent);
    toast('Mermaid diagram copied to clipboard.', 'ok');
  } catch {
    toast('Clipboard access denied.', 'error');
  }
});

// ── View 2: Dependency Graph ─────────────────────────────────────────────────

function openEntityGraph(stateType, entityId) {
  $('g-state-type').value = shortType(stateType);
  $('g-entity-id').value = entityId;
  switchView('graph');
  loadGraph();
}

async function loadGraph(silent = false) {
  const stateType = $('g-state-type').value.trim();
  const entityId = $('g-entity-id').value.trim();
  if (!stateType || !entityId) {
    if (!silent) toast('Enter StateType and Entity ID to build a graph.', 'error');
    return;
  }
  const depth = Number($('g-depth').value) || 2;
  const windowSize = Number($('g-window').value) || 5000;
  try {
    const data = await mcp.entityGraph({ stateType, entityId, depth, window: windowSize });
    state.graphData = data;
    graphView.setData(data);
  } catch (err) {
    if (!silent) toast(err.message, 'error');
  }
}

$('g-load-btn').addEventListener('click', () => loadGraph());
$('g-mermaid-btn').addEventListener('click', () => {
  if (!state.graphData?.mermaid) { toast('Build a graph first.', 'error'); return; }
  $('mermaid-title').textContent = `Graph Mermaid — ${shortType(state.graphData.center)}`;
  $('mermaid-code').textContent = state.graphData.mermaid;
  openModal('modal-mermaid');
});

// ── View 3: Live Event Stream & Call Tree ────────────────────────────────────

async function loadStream(silent = false) {
  const filter = {
    entityId: $('st-entity').value.trim() || undefined,
    stateType: $('st-state-type').value.trim() || undefined,
    playerId: $('st-player').value.trim() || undefined,
    method: $('st-method').value.trim() || undefined,
    kind: $('st-kind').value || undefined,
    callKind: $('st-call-kind').value || undefined,
    errorsOnly: $('st-errors-only').checked || undefined,
    includePayloads: $('st-payloads').checked,
    limit: 120,
  };
  try {
    const res = await mcp.recentCalls(filter);
    state.streamEvents = res?.events || [];
    renderStreamTable();
  } catch (err) {
    if (!silent) toast(err.message, 'error');
  }
}

function renderStreamTable() {
  const tbody = $('stream-tbody');
  if (state.streamEvents.length === 0) {
    tbody.innerHTML = `<tr><td colspan="10" class="muted-note">No matching events captured.</td></tr>`;
    return;
  }

  tbody.innerHTML = state.streamEvents.map((ev, idx) => {
    const isErr = ev.result && ev.result !== 'success';
    const selected = state.selectedStreamEvent?.id === ev.id;
    const entityLabel = ev.entityKey ? shortType(ev.entityKey) : '—';
    const methodLabel = ev.service && ev.method ? `${ev.service}.${ev.method}`
      : ev.callKind === 'init' ? `[MetaInit] v${ev.fromVersion}→v${ev.toVersion}` : (ev.method || '—');
    const party = ev.targetEntity ? `→ ${shortType(ev.targetEntity)}`
      : ev.callerEntity ? `${shortType(ev.callerEntity)} →`
      : ev.callerId ? `player:${ev.callerId}`
      : ev.targetPlayer ? `→ player:${ev.targetPlayer}`
      : ev.subscriberCount != null ? `×${ev.subscriberCount} subs` : '—';
    const shortCall = ev.callId ? ev.callId.slice(0, 10) + '…' : '—';

    return `
      <tr data-idx="${idx}" class="${isErr ? 'is-error' : ''} ${selected ? 'is-selected' : ''}">
        <td>${ev.id ?? ''}</td>
        <td>${formatTime(ev.startedAtUtc)}</td>
        <td><span class="badge ${kindBadgeClass(ev)}">${escapeHtml(ev.callKind || ev.kind)}</span></td>
        <td>${escapeHtml(entityLabel)}</td>
        <td>#${ev.entitySequence}.${ev.subOrder}</td>
        <td>${escapeHtml(methodLabel)}</td>
        <td>${escapeHtml(party)}</td>
        <td><span class="badge ${isErr ? 'badge-error' : 'badge-ok'}">${escapeHtml(ev.result || 'ok')}</span></td>
        <td>${formatMs(ev.durationMs)}</td>
        <td>${ev.callId ? `<button type="button" class="link-btn" data-call-id="${escapeHtml(ev.callId)}">${escapeHtml(shortCall)}</button>` : '—'}</td>
      </tr>
    `;
  }).join('');

  tbody.querySelectorAll('tr[data-idx]').forEach(tr => {
    tr.addEventListener('click', () => {
      const ev = state.streamEvents[Number(tr.dataset.idx)];
      state.selectedStreamEvent = ev;
      renderStreamTable();
      renderStreamDetail(ev);
    });
  });

  tbody.querySelectorAll('[data-call-id]').forEach(btn => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      openCallTree(btn.getAttribute('data-call-id'));
    });
  });
}

function renderStreamDetail(ev) {
  const el = $('stream-detail');
  if (!ev) return;
  const { stateType, entityId } = splitEntityKey(ev.entityKey || '');
  el.innerHTML = `
    <div class="kv-section">
      <div class="kv-title">${escapeHtml(ev.kind)} · ${escapeHtml(ev.callKind || '')}</div>
      <div class="card-actions" style="margin:6px 0;">
        ${ev.callId ? `<button type="button" class="btn btn-xs btn-cyan" id="sd-call-tree">Open Call Tree</button>` : ''}
        ${stateType && entityId ? `<button type="button" class="btn btn-xs btn-amber" id="sd-timeline">Open Entity Timeline</button>` : ''}
      </div>
      <div class="kv-body" id="sd-json"></div>
    </div>
  `;
  renderJsonTree(el.querySelector('#sd-json'), ev, { maxDepthExpand: 3 });
  el.querySelector('#sd-call-tree')?.addEventListener('click', () => openCallTree(ev.callId));
  el.querySelector('#sd-timeline')?.addEventListener('click', () => openEntityTimeline(stateType, entityId));
}

function kindBadgeClass(ev) {
  if (ev.result && ev.result !== 'success') return 'badge-error';
  if (ev.kind === 'client_responded' || ev.kind === 'broadcast_sent') return 'badge-ok';
  if (ev.kind === 'call_sent' || ev.callKind === 'cross_entity') return 'badge-amber';
  if (ev.callKind === 'trigger' || ev.callKind === 'nested' || ev.callKind === 'init') return 'badge-violet';
  return 'badge-cyan';
}

$('st-load-btn').addEventListener('click', () => loadStream());
$('st-lookup-call-btn').addEventListener('click', () => {
  const cid = $('st-call-id-input').value.trim();
  if (!cid) { toast('Enter a CallId first.', 'error'); return; }
  openCallTree(cid);
});

async function openCallTree(callId) {
  try {
    const tree = await mcp.getCall(callId, true);
    $('ct-subtitle').textContent = `CallId ${tree.callId} · ${tree.events ?? 0} event(s)`;
    const treeEl = $('ct-tree');
    const inspEl = $('ct-inspector');
    treeEl.innerHTML = '';
    inspEl.innerHTML = `<div class="muted-note">Select a span event to inspect its payload and state.</div>`;

    const selectSpanEv = (btn, ev) => {
      treeEl.querySelectorAll('.ct-event-btn').forEach(b => b.classList.remove('is-selected'));
      btn.classList.add('is-selected');
      inspEl.innerHTML = '';
      renderJsonTree(inspEl, ev, { maxDepthExpand: 3 });
    };

    let firstBtn = null;
    let firstEv = null;

    const renderNode = (node, container) => {
      const wrap = document.createElement('div');
      wrap.className = 'ct-span';
      const sorted = [...(node.events || [])].sort((a, b) =>
        (a.entitySequence - b.entitySequence) || (a.subOrder - b.subOrder) || ((a.id ?? 0) - (b.id ?? 0)));
      for (const ev of sorted) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'ct-event-btn';
        const isErr = ev.result && ev.result !== 'success';
        const title = `${ev.kind === 'call_sent' ? '→ ' : ''}${ev.service ? ev.service + '.' : ''}${ev.method || ev.kind}`;
        btn.innerHTML = `
          <span>
            <span class="badge ${kindBadgeClass(ev)}">${escapeHtml(ev.callKind || ev.kind)}</span>
            <strong>${escapeHtml(title)}</strong>
            <span class="muted-note">${escapeHtml(shortType(ev.entityKey || ''))} #${ev.entitySequence}</span>
          </span>
          <span class="badge ${isErr ? 'badge-error' : 'badge-neutral'}">${formatMs(ev.durationMs)}</span>
        `;
        btn.addEventListener('click', () => selectSpanEv(btn, ev));
        wrap.appendChild(btn);
        if (!firstBtn) { firstBtn = btn; firstEv = ev; }
      }
      for (const child of node.children || []) {
        renderNode(child, wrap);
      }
      container.appendChild(wrap);
    };

    for (const root of tree.roots || []) renderNode(root, treeEl);
    if ((tree.unspanned || []).length > 0) {
      const title = document.createElement('div');
      title.className = 'kv-title';
      title.style.marginTop = '8px';
      title.textContent = 'Unspanned (e.g. client_responded)';
      treeEl.appendChild(title);
      renderNode({ events: tree.unspanned, children: [] }, treeEl);
    }

    if (firstBtn && firstEv) selectSpanEv(firstBtn, firstEv);
    openModal('modal-call-tree');
  } catch (err) {
    toast(err.message, 'error');
  }
}

// ── View 4: Entities, Sessions & Live State ──────────────────────────────────

async function loadEntities() {
  try {
    const st = $('ent-state-filter').value.trim() || undefined;
    const res = await mcp.listEntities(st, 300);
    state.entities = res?.entities || [];
    renderEntitiesTables();
    renderSidebar();
  } catch (err) {
    toast(err.message, 'error');
  }
}

async function loadSessions() {
  try {
    const res = await mcp.listSessions();
    state.sessions = Array.isArray(res) ? res : [];
    renderEntitiesTables();
  } catch (err) {
    toast(err.message, 'error');
  }
}

function renderEntitiesTables() {
  const entTbody = $('ent-tbody');
  if (entTbody) {
    if (state.entities.length === 0) {
      entTbody.innerHTML = `<tr><td colspan="5" class="muted-note">No seen entities.</td></tr>`;
    } else {
      entTbody.innerHTML = state.entities.map(e => {
        const watched = state.capture?.watchedEntities?.[e.entityId];
        return `
          <tr>
            <td title="${escapeHtml(e.stateType)}">${escapeHtml(shortType(e.stateType))}</td>
            <td><strong>${escapeHtml(e.entityId)}</strong> ${watched ? `<span class="badge badge-amber">${escapeHtml(watched)}</span>` : ''}</td>
            <td>#${e.lastSequence}</td>
            <td>${formatTime(e.lastSeenUtc)}</td>
            <td>
              <button type="button" class="btn btn-xs btn-amber" data-act="tl" data-st="${escapeHtml(e.stateType)}" data-id="${escapeHtml(e.entityId)}">Timeline</button>
              <button type="button" class="btn btn-xs btn-cyan" data-act="graph" data-st="${escapeHtml(e.stateType)}" data-id="${escapeHtml(e.entityId)}">Graph</button>
              <button type="button" class="btn btn-xs" data-act="state" data-st="${escapeHtml(e.stateType)}" data-id="${escapeHtml(e.entityId)}">State</button>
              <button type="button" class="btn btn-xs" data-act="watch" data-id="${escapeHtml(e.entityId)}">${watched ? 'Unwatch' : 'Watch'}</button>
            </td>
          </tr>
        `;
      }).join('');

      entTbody.querySelectorAll('button[data-act]').forEach(btn => {
        btn.addEventListener('click', async () => {
          const act = btn.dataset.act;
          const st = btn.dataset.st;
          const id = btn.dataset.id;
          if (act === 'tl') openEntityTimeline(st, id);
          else if (act === 'graph') openEntityGraph(st, id);
          else if (act === 'state') openLiveState(st, id);
          else if (act === 'watch') {
            if (state.capture?.watchedEntities?.[id]) await unwatchEntity(id);
            else await quickWatchEntity(id, 'Full');
          }
        });
      });
    }
  }

  const sessTbody = $('sess-tbody');
  if (sessTbody) {
    if (state.sessions.length === 0) {
      sessTbody.innerHTML = `<tr><td colspan="5" class="muted-note">No open sessions on this host.</td></tr>`;
    } else {
      sessTbody.innerHTML = state.sessions.map(s => {
        const watched = state.capture?.watchedPlayers?.[s.playerId];
        return `
          <tr>
            <td><strong>${escapeHtml(s.playerId)}</strong> ${watched ? `<span class="badge badge-cyan">${escapeHtml(watched)}</span>` : ''}</td>
            <td title="${escapeHtml(s.sessionId)}">${escapeHtml((s.sessionId || '').slice(0, 14))}…</td>
            <td>${escapeHtml(s.clientVersion || '—')} ${s.resumed ? '<span class="badge badge-neutral">resumed</span>' : ''}</td>
            <td>${formatTime(s.startedAtUtc)}</td>
            <td>
              <button type="button" class="btn btn-xs btn-cyan" data-sess-watch="${escapeHtml(s.playerId)}">${watched ? 'Unwatch' : 'Watch'}</button>
              <button type="button" class="btn btn-xs" data-sess-calls="${escapeHtml(s.playerId)}">Calls</button>
            </td>
          </tr>
        `;
      }).join('');

      sessTbody.querySelectorAll('[data-sess-watch]').forEach(btn => {
        btn.addEventListener('click', async () => {
          const pid = btn.getAttribute('data-sess-watch');
          if (state.capture?.watchedPlayers?.[pid]) await unwatchPlayer(pid);
          else await quickWatchPlayer(pid, 'ArgsResult');
        });
      });
      sessTbody.querySelectorAll('[data-sess-calls]').forEach(btn => {
        btn.addEventListener('click', () => {
          $('st-player').value = btn.getAttribute('data-sess-calls');
          switchView('stream');
          loadStream();
        });
      });
    }
  }
}

function openLiveState(stateType, entityId) {
  $('ls-state-type').value = shortType(stateType);
  $('ls-entity-id').value = entityId;
  $('ls-path').value = '';
  switchView('entities');
  readLiveState();
}

async function readLiveState() {
  const stateType = $('ls-state-type').value.trim();
  const entityId = $('ls-entity-id').value.trim();
  const path = $('ls-path').value.trim() || undefined;
  const activate = $('ls-activate').checked;
  if (!stateType || !entityId) { toast('Enter StateType and Entity ID.', 'error'); return; }

  const out = $('ls-result');
  out.innerHTML = `<div class="muted-note">Reading live grain state…</div>`;
  try {
    const res = await mcp.getEntityState(stateType, entityId, path, activate);
    out.innerHTML = `
      <div class="state-meta-row" style="margin-bottom:8px;">
        <span><code>${escapeHtml(res.entity)}</code> ${res.path ? `· path <code>${escapeHtml(res.path)}</code>` : ''}</span>
        ${res.path ? `<button type="button" class="btn btn-xs" id="ls-clear-path">Reset Path</button>` : ''}
      </div>
      <div id="ls-tree"></div>
    `;
    renderJsonTree(out.querySelector('#ls-tree'), res.state, {
      maxDepthExpand: 3,
      onPathClick: (clickedPath) => {
        const fullPath = res.path ? `${res.path}.${clickedPath}` : clickedPath;
        $('ls-path').value = fullPath;
        readLiveState();
      },
    });
    out.querySelector('#ls-clear-path')?.addEventListener('click', () => {
      $('ls-path').value = '';
      readLiveState();
    });
  } catch (err) {
    out.innerHTML = `<div class="card-error-box">${escapeHtml(err.message)}</div>`;
  }
}

$('ent-refresh-btn').addEventListener('click', () => loadEntities());
$('sess-refresh-btn').addEventListener('click', () => loadSessions());
$('ls-load-btn').addEventListener('click', () => readLiveState());

// ── View 5: Methods Schema & Invoke ──────────────────────────────────────────

async function loadSchema() {
  try {
    state.schema = await mcp.listMethods();
    updateDatalists();
    renderMethodsCatalog();
  } catch (err) {
    toast(err.message, 'error');
  }
}

$('meth-search').addEventListener('input', (e) => {
  state.methodSearch = e.target.value.trim().toLowerCase();
  renderMethodsCatalog();
});

function renderMethodsCatalog() {
  const catalog = $('meth-catalog');
  const noteEl = $('meth-note');
  if (!state.schema) return;

  if (state.schema.note) {
    noteEl.hidden = false;
    noteEl.textContent = state.schema.note;
  } else {
    noteEl.hidden = true;
  }

  const methods = (state.schema.methods || []).filter(m => {
    if (!state.methodSearch) return true;
    return `${m.service}.${m.method} ${m.csharpName} ${m.stateType} ${m.mode}`.toLowerCase().includes(state.methodSearch);
  });

  if (methods.length === 0) {
    catalog.innerHTML = `<div class="muted-note">No methods available in schema.</div>`;
    return;
  }

  const byService = new Map();
  for (const m of methods) {
    if (!byService.has(m.service)) byService.set(m.service, []);
    byService.get(m.service).push(m);
  }

  catalog.innerHTML = [...byService.entries()].map(([svc, list]) => `
    <div>
      <div class="service-group-title">${escapeHtml(svc)}</div>
      ${list.map(m => {
        const params = (m.parameters || []).map(p => `${p.name}: ${shortType(p.wireType)}${p.transformed ? ' [wire]' : ''}`).join(', ');
        const ret = m.result ? shortType(m.result) : 'void';
        const sel = state.selectedMethod?.id === m.id;
        return `
          <button type="button" class="method-row ${sel ? 'is-selected' : ''}" data-method-id="${m.id}">
            <div class="method-row-top">
              <span class="method-name">${escapeHtml(m.method)} <span class="muted-note">v${m.version}</span></span>
              <span>
                <span class="badge ${m.mode === 'LocalQuery' ? 'badge-neutral' : m.mode === 'Query' ? 'badge-cyan' : 'badge-amber'}">${escapeHtml(m.mode || 'Default')}</span>
              </span>
            </div>
            <div class="method-sig">(${escapeHtml(params)}) → ${escapeHtml(ret)} · ${escapeHtml(shortType(m.stateType))}</div>
          </button>
        `;
      }).join('')}
    </div>
  `).join('');

  catalog.querySelectorAll('.method-row').forEach(btn => {
    btn.addEventListener('click', () => {
      const id = Number(btn.dataset.methodId);
      state.selectedMethod = (state.schema.methods || []).find(m => m.id === id) || null;
      renderMethodsCatalog();
      renderInvokeForm();
    });
  });
}

function renderInvokeForm() {
  const el = $('invoke-body');
  const m = state.selectedMethod;
  if (!m) {
    el.innerHTML = `<div class="muted-note">Pick a method from the catalog on the left.</div>`;
    return;
  }

  const params = m.parameters || [];
  const defaultEntity = $('tl-entity-id').value.trim()
    || state.entities.find(e => e.stateType === m.stateType)?.entityId
    || '';
  const disabledReason = !state.status?.invokeAllowed
    ? 'Server has InspectorOptions.AllowInvoke = false.'
    : m.mode === 'LocalQuery'
      ? 'LocalQuery methods run on the client only and cannot be invoked on the server.'
      : null;

  el.innerHTML = `
    <div class="code-sig-box">${escapeHtml(m.service)}.${escapeHtml(m.method)} (v${m.version}) · ${escapeHtml(m.mode || 'Default')}</div>
    ${disabledReason ? `<div class="card-error-box">${escapeHtml(disabledReason)}</div>` : ''}

    <div class="form-row">
      <label>State Type</label>
      <input type="text" id="inv-state-type" class="input" value="${escapeHtml(shortType(m.stateType))}" />
    </div>
    <div class="form-row">
      <label>Entity ID</label>
      <input type="text" id="inv-entity-id" class="input" list="datalist-entity-ids" value="${escapeHtml(defaultEntity)}" placeholder="Target entity grain key" />
    </div>
    <div style="display:grid; grid-template-columns:1fr 1fr; gap:8px;">
      <div class="form-row">
        <label>Caller ID (optional)</label>
        <input type="text" id="inv-caller-id" class="input" placeholder="inspector" />
      </div>
      <div class="form-row">
        <label>Caller Client Version (optional)</label>
        <input type="text" id="inv-client-ver" class="input" placeholder="e.g. 2.0.0" />
      </div>
    </div>

    ${params.length > 0 ? `
      <div class="form-row">
        <div style="display:flex; justify-content:space-between; align-items:center;">
          <label>Arguments (${params.length})</label>
          <button type="button" class="btn btn-xs" id="inv-toggle-mode">${state.invokeMode === 'form' ? 'Switch to Raw JSON' : 'Switch to Form'}</button>
        </div>
        ${state.invokeMode === 'form' ? params.map(p => `
          <div class="form-row" style="margin-top:4px;">
            <label>
              <code>${escapeHtml(p.name)}</code>
              <span class="muted-note">wire: ${escapeHtml(shortType(p.wireType))}${p.transformed ? ` (declared: ${escapeHtml(p.declaredType)})` : ''}</span>
            </label>
            <input type="text" class="input inv-param" data-param="${escapeHtml(p.name)}" data-wire="${escapeHtml(p.wireType)}" placeholder="${escapeHtml(samplePlaceholder(p.wireType))}" />
          </div>
        `).join('') : `
          <textarea id="inv-args-json" class="input" placeholder='{"param": value}'></textarea>
        `}
      </div>
    ` : `<div class="muted-note">This method takes no arguments.</div>`}

    <div class="toolbar-group">
      <button type="button" class="btn btn-amber" id="inv-run-btn" ${disabledReason ? 'disabled' : ''}>Execute Method</button>
    </div>

    <div id="inv-output"></div>
  `;

  el.querySelector('#inv-toggle-mode')?.addEventListener('click', () => {
    state.invokeMode = state.invokeMode === 'form' ? 'json' : 'form';
    renderInvokeForm();
  });

  el.querySelector('#inv-run-btn')?.addEventListener('click', async () => {
    const stateType = $('inv-state-type').value.trim();
    const entityId = $('inv-entity-id').value.trim();
    const callerId = $('inv-caller-id').value.trim() || undefined;
    const callerClientVersion = $('inv-client-ver').value.trim() || undefined;
    if (!entityId) { toast('Entity ID is required.', 'error'); return; }

    let args;
    if (params.length > 0) {
      if (state.invokeMode === 'json') {
        const raw = $('inv-args-json').value.trim();
        try { args = raw ? JSON.parse(raw) : {}; }
        catch (e) { toast(`Invalid JSON args: ${e.message}`, 'error'); return; }
      } else {
        args = {};
        for (const input of el.querySelectorAll('.inv-param')) {
          const pName = input.dataset.param;
          const wire = input.dataset.wire || '';
          const raw = input.value.trim();
          try {
            args[pName] = coerceArgValue(raw, wire);
          } catch (e) {
            toast(`Invalid value for '${pName}': ${e.message}`, 'error');
            return;
          }
        }
      }
    }

    const out = $('inv-output');
    out.innerHTML = `<div class="muted-note">Executing ${escapeHtml(m.service)}.${escapeHtml(m.method)}…</div>`;
    try {
      const res = await mcp.invokeMethod({
        stateType,
        entityId,
        service: m.service,
        method: m.method,
        version: m.version,
        args,
        callerId,
        callerClientVersion,
      });
      out.innerHTML = `
        <div class="kv-section">
          <div style="display:flex; align-items:center; justify-content:space-between; gap:8px;">
            <span class="badge ${res.success ? 'badge-ok' : 'badge-error'}">${res.success ? '✓ Success' : '✗ Failed'}</span>
            <div class="toolbar-group">
              ${res.callId ? `<button type="button" class="btn btn-xs btn-cyan" id="inv-inspect-call">Inspect Call Tree</button>` : ''}
              <button type="button" class="btn btn-xs btn-amber" id="inv-open-tl">Open Timeline</button>
            </div>
          </div>
          ${res.error ? `<div class="card-error-box">${escapeHtml(res.error)}</div>` : ''}
          ${res.note ? `<div class="muted-note">${escapeHtml(res.note)}</div>` : ''}
          <div class="kv-body" id="inv-res-tree"></div>
        </div>
      `;
      renderJsonTree(out.querySelector('#inv-res-tree'), res, { maxDepthExpand: 3 });
      out.querySelector('#inv-inspect-call')?.addEventListener('click', () => openCallTree(res.callId));
      out.querySelector('#inv-open-tl')?.addEventListener('click', () => openEntityTimeline(stateType, entityId));
      toast(`Invoked ${m.service}.${m.method}`, res.success ? 'ok' : 'error');
      refreshActiveView(true);
    } catch (err) {
      out.innerHTML = `<div class="card-error-box">${escapeHtml(err.message)}</div>`;
    }
  });
}

const INT_WIRE_TYPES = new Set([
  'system.byte', 'byte',
  'system.sbyte', 'sbyte',
  'system.int16', 'short',
  'system.uint16', 'ushort',
  'system.int32', 'int',
  'system.uint32', 'uint',
  'system.int64', 'long',
  'system.uint64', 'ulong',
]);

const FLOAT_WIRE_TYPES = new Set([
  'system.single', 'float',
  'system.double', 'double',
  'system.decimal', 'decimal',
]);

const BOOL_WIRE_TYPES = new Set(['system.boolean', 'bool']);
const STRING_WIRE_TYPES = new Set(['system.string', 'string']);

function unwrapNullableWireType(wireType) {
  let t = (wireType || '').trim().toLowerCase();
  let nullable = false;
  if (t.endsWith('?')) {
    nullable = true;
    t = t.slice(0, -1).trim();
  }
  const m = t.match(/^(?:system\.)?nullable<(.+)>$/);
  if (m) {
    nullable = true;
    t = m[1].trim();
  }
  return { base: t, nullable };
}

function samplePlaceholder(wireType) {
  const { base, nullable } = unwrapNullableWireType(wireType);
  if (INT_WIRE_TYPES.has(base)) return nullable ? '0 (or empty for null)' : '0';
  if (FLOAT_WIRE_TYPES.has(base)) return nullable ? '0.0 (or empty for null)' : '0.0';
  if (BOOL_WIRE_TYPES.has(base)) return nullable ? 'true / false / empty' : 'true / false';
  if (STRING_WIRE_TYPES.has(base)) return 'text value';
  return 'JSON value (e.g. {"x": 1} or [1, 2])';
}

function coerceArgValue(raw, wireType) {
  const { base, nullable } = unwrapNullableWireType(wireType);
  if (STRING_WIRE_TYPES.has(base)) {
    if (raw.startsWith('"') && raw.endsWith('"')) return JSON.parse(raw);
    return raw;
  }
  if (BOOL_WIRE_TYPES.has(base)) {
    if (raw === '' && nullable) return null;
    if (raw === 'true') return true;
    if (raw === 'false') return false;
    throw new Error('expected true or false');
  }
  if (INT_WIRE_TYPES.has(base) || FLOAT_WIRE_TYPES.has(base)) {
    if (raw === '') {
      if (nullable) return null;
      throw new Error('number required');
    }
    const n = Number(raw);
    if (!Number.isFinite(n)) throw new Error('invalid number');
    if (INT_WIRE_TYPES.has(base) && !Number.isInteger(n)) throw new Error('expected integer');
    return n;
  }
  if (raw === '') return null;
  return JSON.parse(raw);
}

// ── View 6: Capture Config & Telemetry ───────────────────────────────────────

function syncCaptureDraft(capture) {
  state.captureDraft = {
    level: capture?.level || 'Off',
    sampleRate: capture?.sampleRate ?? 1,
    broadcastPerSubscriber: Boolean(capture?.broadcastPerSubscriber),
    methodLevels: { ...(capture?.methodLevels || {}) },
  };
  state.captureDirty = false;
}

function ensureCaptureDraft() {
  if (!state.captureDraft) syncCaptureDraft(state.capture);
  return state.captureDraft;
}

function renderCaptureView({ updateForm = true } = {}) {
  const s = state.status;
  const c = state.capture;

  const cardsEl = $('cap-status-cards');
  if (cardsEl && s) {
    cardsEl.innerHTML = `
      <div class="stat-card">
        <div class="stat-label">Capture Tap</div>
        <div class="stat-val" style="color:${s.captureAvailable ? 'var(--green)' : 'var(--amber)'}">${s.captureAvailable ? 'Available' : 'Unavailable'}</div>
        <div class="stat-sub">${escapeHtml(s.captureNote || s.silo || '')}</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">Stored Events</div>
        <div class="stat-val">${s.storedEvents ?? 0}</div>
        <div class="stat-sub">${formatBytes(s.storedBytes)} · evicted ${s.evictedEvents ?? 0}</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">Pipeline</div>
        <div class="stat-val">${s.queuedEvents ?? 0} queued</div>
        <div class="stat-sub" style="color:${(s.droppedEvents || 0) > 0 ? 'var(--red)' : 'var(--text-dim)'}">${s.droppedEvents ?? 0} dropped</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">Distribution</div>
        <div class="stat-val">${s.sharedCaptureSettings ? 'Broker' : 'Single Silo'}</div>
        <div class="stat-sub">${escapeHtml(s.distribution || '')}</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">Live &amp; Invoke</div>
        <div class="stat-val">${s.liveAccess ? 'Silo' : 'Read-Only'}</div>
        <div class="stat-sub">Invoke: ${s.invokeAllowed ? 'enabled' : 'disabled'} · History: ${s.history ? 'on' : 'off'}</div>
      </div>
    `;
  }

  if (!c) return;

  if (updateForm) {
    const draft = ensureCaptureDraft();

    // Global Level segmented buttons
    document.querySelectorAll('#cap-level-seg button').forEach(btn => {
      btn.classList.toggle('is-active', btn.dataset.level === draft.level);
    });

    $('cap-sample-rate').value = draft.sampleRate;
    $('cap-sample-val').textContent = `${Math.round(draft.sampleRate * 100)}%`;
    $('cap-broadcast-sub').checked = Boolean(draft.broadcastPerSubscriber);

    const saveBtn = $('cap-save-btn');
    if (saveBtn) saveBtn.textContent = state.captureDirty ? 'Apply Settings *' : 'Apply Settings';

    // Per-method rules
    const rulesEl = $('cap-rules-list');
    const entries = Object.entries(draft.methodLevels || {});
    rulesEl.innerHTML = entries.length === 0
      ? `<div class="muted-note">No per-method overrides.</div>`
      : entries.map(([pat, lvl]) => `
          <div class="rule-row">
            <span>${escapeHtml(pat)}</span>
            <span class="toolbar-group">
              <span class="badge badge-amber">${escapeHtml(lvl)}</span>
              <button type="button" class="btn-icon" data-del-rule="${escapeHtml(pat)}">✕</button>
            </span>
          </div>
        `).join('');

    rulesEl.querySelectorAll('[data-del-rule]').forEach(btn => {
      btn.addEventListener('click', () => {
        delete ensureCaptureDraft().methodLevels[btn.getAttribute('data-del-rule')];
        state.captureDirty = true;
        renderCaptureView();
      });
    });
  }

  // Watched entities (always reflects live server state)
  const weEl = $('cap-watched-entities');
  const we = Object.entries(c.watchedEntities || {});
  weEl.innerHTML = we.length === 0
    ? `<div class="muted-note">No watched entities.</div>`
    : we.map(([id, lvl]) => `
        <div class="rule-row">
          <span>${escapeHtml(id)}</span>
          <span class="toolbar-group">
            <span class="badge badge-amber">${escapeHtml(lvl)}</span>
            <button type="button" class="btn btn-xs btn-danger" data-unwatch-e="${escapeHtml(id)}">Unwatch</button>
          </span>
        </div>
      `).join('');
  weEl.querySelectorAll('[data-unwatch-e]').forEach(btn => {
    btn.addEventListener('click', () => unwatchEntity(btn.getAttribute('data-unwatch-e')));
  });

  // Watched players (always reflects live server state)
  const wpEl = $('cap-watched-players');
  const wp = Object.entries(c.watchedPlayers || {});
  wpEl.innerHTML = wp.length === 0
    ? `<div class="muted-note">No watched players.</div>`
    : wp.map(([id, lvl]) => `
        <div class="rule-row">
          <span>player:${escapeHtml(id)}</span>
          <span class="toolbar-group">
            <span class="badge badge-cyan">${escapeHtml(lvl)}</span>
            <button type="button" class="btn btn-xs btn-danger" data-unwatch-p="${escapeHtml(id)}">Unwatch</button>
          </span>
        </div>
      `).join('');
  wpEl.querySelectorAll('[data-unwatch-p]').forEach(btn => {
    btn.addEventListener('click', () => unwatchPlayer(btn.getAttribute('data-unwatch-p')));
  });
}

document.querySelectorAll('#cap-level-seg button').forEach(btn => {
  btn.addEventListener('click', () => {
    ensureCaptureDraft().level = btn.dataset.level;
    state.captureDirty = true;
    renderCaptureView();
  });
});

$('cap-sample-rate').addEventListener('input', (e) => {
  const val = Number(e.target.value);
  ensureCaptureDraft().sampleRate = val;
  state.captureDirty = true;
  $('cap-sample-val').textContent = `${Math.round(val * 100)}%`;
  const saveBtn = $('cap-save-btn');
  if (saveBtn) saveBtn.textContent = 'Apply Settings *';
});

$('cap-broadcast-sub').addEventListener('change', (e) => {
  ensureCaptureDraft().broadcastPerSubscriber = e.target.checked;
  state.captureDirty = true;
  const saveBtn = $('cap-save-btn');
  if (saveBtn) saveBtn.textContent = 'Apply Settings *';
});

$('cap-rule-add').addEventListener('click', () => {
  const pat = $('cap-rule-pattern').value.trim();
  const lvl = $('cap-rule-level').value;
  if (!pat) { toast('Enter a method pattern (e.g. IShopService.Buy or IShopService.*).', 'error'); return; }
  ensureCaptureDraft().methodLevels[pat] = lvl;
  state.captureDirty = true;
  $('cap-rule-pattern').value = '';
  renderCaptureView();
});

$('cap-save-btn').addEventListener('click', async () => {
  const draft = ensureCaptureDraft();
  try {
    const updated = await mcp.setCaptureConfig({
      level: draft.level,
      methodLevels: draft.methodLevels,
      sampleRate: draft.sampleRate,
      broadcastPerSubscriber: draft.broadcastPerSubscriber,
    });
    state.capture = updated;
    syncCaptureDraft(updated);
    state.status = await mcp.status();
    updateStatusUi();
    renderSidebar();
    renderCaptureView();
    toast('Capture configuration applied.', 'ok');
  } catch (err) {
    toast(err.message, 'error');
  }
});

$('cap-watch-entity-btn').addEventListener('click', async () => {
  const id = $('cap-watch-entity-id').value.trim();
  const lvl = $('cap-watch-entity-level').value;
  if (!id) { toast('Enter an Entity ID.', 'error'); return; }
  await quickWatchEntity(id, lvl);
  $('cap-watch-entity-id').value = '';
});

$('cap-watch-player-btn').addEventListener('click', async () => {
  const id = $('cap-watch-player-id').value.trim();
  const lvl = $('cap-watch-player-level').value;
  if (!id) { toast('Enter a Player ID.', 'error'); return; }
  await quickWatchPlayer(id, lvl);
  $('cap-watch-player-id').value = '';
});

async function quickWatchEntity(entityId, level) {
  try {
    state.capture = await mcp.watchEntity(entityId, level);
    state.status = await mcp.status();
    updateStatusUi();
    renderSidebar();
    renderCaptureView();
    renderEntitiesTables();
    toast(`Watching entity '${entityId}' at ${level}.`, 'ok');
  } catch (err) {
    toast(err.message, 'error');
  }
}

async function unwatchEntity(entityId) {
  try {
    state.capture = await mcp.unwatchEntity(entityId);
    state.status = await mcp.status();
    updateStatusUi();
    renderSidebar();
    renderCaptureView();
    renderEntitiesTables();
    toast(`Stopped watching entity '${entityId}'.`, 'ok');
  } catch (err) {
    toast(err.message, 'error');
  }
}

async function quickWatchPlayer(playerId, level) {
  try {
    state.capture = await mcp.watchPlayer(playerId, level);
    state.status = await mcp.status();
    updateStatusUi();
    renderSidebar();
    renderCaptureView();
    renderEntitiesTables();
    toast(`Watching player '${playerId}' at ${level}.`, 'ok');
  } catch (err) {
    toast(err.message, 'error');
  }
}

async function unwatchPlayer(playerId) {
  try {
    state.capture = await mcp.unwatchPlayer(playerId);
    state.status = await mcp.status();
    updateStatusUi();
    renderSidebar();
    renderCaptureView();
    renderEntitiesTables();
    toast(`Stopped watching player '${playerId}'.`, 'ok');
  } catch (err) {
    toast(err.message, 'error');
  }
}

// ── Boot ─────────────────────────────────────────────────────────────────────

bootstrapAll();
