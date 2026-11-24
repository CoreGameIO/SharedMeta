import { escapeHtml, shortType, splitEntityKey, formatMs, formatTime, renderJsonTree, renderDiffList } from './json-view.js';

/**
 * Renders the SharedMeta TimeMachine Swimlane Timeline styled after SharedMeta's
 * Optimistic Execution & Deterministic Replay visual language:
 *   - Top lane:    CLIENT TIMELINE (cyan incoming requests, emerald ✓ replies & broadcasts, subscriptions)
 *   - Middle lane: SERVER TIMELINE (ORLEANS) (gold-bordered authoritative executions, triggers, inits)
 *   - Bottom lane: CROSS-ENTITY TIMELINE (outgoing call_sent & incoming cross-entity hops, shown when present)
 *   - Left card:   Event & State Inspector card with golden header, causal link highlighting & state diff
 */
export class TimelineView {
  constructor({ canvasEl, cardEl, scrubberEl, callbacks }) {
    this.canvasEl = canvasEl;
    this.cardEl = cardEl;
    this.scrubberEl = scrubberEl;
    this.callbacks = callbacks;

    this.entityKey = '';
    this.events = [];
    this.groups = [];
    this.selectedEventKey = null;
    this.activeCardTab = 'payload'; // 'payload' | 'diff' | 'state' | 'meta'
    this.zoom = 'normal'; // 'compact' | 'normal' | 'detailed'
    this._stateCache = new Map(); // seq -> { state, actualSeq, isExact }
    this._dragPan = null;
  }

  setZoom(zoom) {
    this.zoom = zoom;
    this.render();
  }

  setData(entityKey, events) {
    this.entityKey = entityKey;
    this.events = Array.isArray(events) ? events : [];
    this._stateCache.clear();

    // Seed local state cache from captured Full-level events
    for (const ev of this.events) {
      if (ev.state !== undefined && ev.state !== null) {
        this._stateCache.set(ev.entitySequence, {
          state: ev.state,
          actualSeq: ev.entitySequence,
          isExact: true,
        });
      }
    }

    this.groups = groupEventsIntoColumns(this.events);

    // Keep previous selection if still present, else select newest primary event
    const stillExists = this.selectedEventKey && this.events.some(e => eventUid(e) === this.selectedEventKey);
    if (!stillExists) {
      const lastPrimary = [...this.events].reverse().find(e => e.kind === 'call_completed') || this.events[this.events.length - 1];
      this.selectedEventKey = lastPrimary ? eventUid(lastPrimary) : null;
    }

    this.render();
  }

  selectEvent(ev) {
    this.selectedEventKey = ev ? eventUid(ev) : null;
    this._applySelectionStyles();
    this._renderCard();
    this._renderScrubber();
    if (this.callbacks.onSelectEvent) this.callbacks.onSelectEvent(ev);
  }

  selectGroupIndex(index) {
    const group = this.groups[index];
    if (!group) return;
    const target = group.serverEvents[0] || group.allEvents[0];
    if (target) {
      this.selectEvent(target);
      const colEl = this.canvasEl.querySelector(`[data-col-idx="${group.index}"]`);
      if (colEl) colEl.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
    }
  }

  selectSequence(seq) {
    const idx = this.groups.findIndex(g => g.seq === seq);
    if (idx >= 0) this.selectGroupIndex(idx);
  }

  render() {
    this._renderCanvas();
    this._renderCard();
    this._renderScrubber();
  }

  _getSelectedEvent() {
    return this.events.find(e => eventUid(e) === this.selectedEventKey) || null;
  }

  _renderCanvas() {
    const el = this.canvasEl;
    if (!this.entityKey) {
      el.innerHTML = `
        <div class="tl-empty">
          <div class="tl-empty-art">
            <div class="tl-demo-lane tl-demo-client">CLIENT TIMELINE</div>
            <div class="tl-demo-pills">
              <span class="pill pill-client">Run Locally (0 ms)</span>
              <span class="tl-demo-dash">⇢</span>
              <span class="pill pill-server">Authoritative + Record</span>
              <span class="tl-demo-dash tl-demo-dash-green">⇢</span>
              <span class="pill pill-verified">✓ Verified &amp; Synced</span>
            </div>
            <div class="tl-demo-lane tl-demo-server">SERVER TIMELINE (ORLEANS)</div>
          </div>
          <h3>Select an entity to inspect its TimeMachine timeline</h3>
          <p>Pick an entity from the left sidebar or top bar, or enable <strong>Watch (Full)</strong> and invoke a method to capture live operations.</p>
        </div>
      `;
      return;
    }

    if (this.events.length === 0) {
      el.innerHTML = `
        <div class="tl-empty">
          <h3>No captured events yet for <code>${escapeHtml(shortType(this.entityKey))}</code></h3>
          <p>Capture is off by default. Click <strong>Watch (Full)</strong> above, then run an action on the client or via <strong>Methods &amp; Invoke</strong>.</p>
        </div>
      `;
      return;
    }

    const hasCross = this.groups.some(g => g.crossEvents.length > 0 || g.incomingCross !== null);
    const colWidth = this.zoom === 'compact' ? 250 : this.zoom === 'detailed' ? 380 : 310;
    const padLeft = 212;
    const padRight = 56;
    const totalWidth = Math.max(760, padLeft + this.groups.length * colWidth + padRight);

    const clientY = 74;
    const serverY = 206;
    const crossY = 332;
    const totalHeight = hasCross ? 406 : 290;

    const nodes = [];
    const edges = [];
    const seqLines = [];

    this.groups.forEach((group, idx) => {
      const colX = padLeft + idx * colWidth;
      const centerX = colX + colWidth * 0.5;
      const leftX = colX + colWidth * 0.20;
      const rightX = colX + colWidth * 0.80;

      seqLines.push({ x: colX + 12, seq: group.seq, idx, hasState: group.hasState });

      // Server primary node(s)
      const serverNodes = [];
      const sCount = group.serverEvents.length;
      group.serverEvents.forEach((ev, sIdx) => {
        const offset = sCount === 1 ? 0 : (sIdx - (sCount - 1) / 2) * Math.min(138, (colWidth - 40) / sCount);
        const nx = centerX + offset;
        const ny = serverY + (ev.callKind === 'trigger' || ev.callKind === 'nested' ? 28 : 0);
        const node = {
          id: `s-${idx}-${sIdx}`,
          ev,
          x: nx,
          y: ny,
          lane: 'server',
          variant: classifyServerVariant(ev),
          title: serverPillTitle(ev),
          subtitle: serverPillSubtitle(ev, this.zoom),
          hasState: ev.state !== undefined && ev.state !== null,
          callId: ev.callId || '',
        };
        nodes.push(node);
        serverNodes.push(node);
      });

      const primaryServer = serverNodes.find(n => n.ev.callKind !== 'init' && n.ev.callKind !== 'trigger')
        || serverNodes[0] || null;

      // Chain triggers / init within server lane
      for (let i = 1; i < serverNodes.length; i++) {
        edges.push({
          from: serverNodes[i - 1],
          to: serverNodes[i],
          color: '#a78bfa',
          callId: serverNodes[i].callId || serverNodes[i - 1].callId,
          dashed: true,
        });
      }

      // Incoming client RPC synthetic/origin pill (if primary call is client rpc)
      if (group.rpcOrigin) {
        const originNode = {
          id: `c-in-${idx}`,
          ev: group.rpcOrigin,
          x: leftX,
          y: clientY,
          lane: 'client',
          variant: 'client-call',
          title: clientCallTitle(group.rpcOrigin),
          subtitle: group.rpcOrigin.callerId ? `player:${group.rpcOrigin.callerId}` : 'client rpc',
          callId: group.rpcOrigin.callId || '',
          isSyntheticOrigin: true,
        };
        nodes.push(originNode);
        if (primaryServer) {
          edges.push({
            from: originNode,
            to: primaryServer,
            color: '#5bc0eb',
            callId: originNode.callId,
            dashed: true,
          });
        }
      }

      // Subscription / standalone client events
      group.clientSubEvents.forEach((ev, cIdx) => {
        const subNode = {
          id: `c-sub-${idx}-${cIdx}`,
          ev,
          x: centerX + cIdx * 34,
          y: clientY,
          lane: 'client',
          variant: ev.result && ev.result !== 'success' ? 'error' : 'client-sub',
          title: formatSubKind(ev.kind),
          subtitle: ev.targetPlayer || ev.callerId || '',
          callId: ev.callId || '',
        };
        nodes.push(subNode);
        if (primaryServer) {
          edges.push({ from: subNode, to: primaryServer, color: '#5bc0eb', callId: ev.callId || '', dashed: true });
        }
      });

      // Outgoing client replies & broadcasts (right side of Client lane)
      const outClient = group.clientOutEvents;
      outClient.forEach((ev, oIdx) => {
        const isErr = ev.result && ev.result !== 'success';
        const nx = outClient.length === 1 ? rightX : rightX + (oIdx - (outClient.length - 1) / 2) * 32;
        const ny = clientY + (oIdx > 0 ? (oIdx % 2 === 1 ? -28 : 28) : 0);
        const outNode = {
          id: `c-out-${idx}-${oIdx}`,
          ev,
          x: nx,
          y: ny,
          lane: 'client',
          variant: isErr ? 'error' : ev.kind === 'broadcast_sent' ? 'broadcast' : 'verified',
          title: clientOutTitle(ev),
          subtitle: clientOutSubtitle(ev),
          callId: ev.callId || '',
        };
        nodes.push(outNode);
        if (primaryServer) {
          edges.push({
            from: primaryServer,
            to: outNode,
            color: isErr ? '#f87171' : '#34d399',
            callId: ev.callId || primaryServer.callId,
            dashed: true,
          });
        }
      });

      // Incoming cross-entity caller pill (bottom lane left)
      if (group.incomingCross) {
        const callerNode = {
          id: `x-in-${idx}`,
          ev: group.incomingCross,
          x: leftX,
          y: crossY,
          lane: 'cross',
          variant: 'cross-in',
          title: `${shortType(group.incomingCross.callerEntity)} →`,
          subtitle: group.incomingCross.callKind || 'cross_entity',
          callId: group.incomingCross.callId || '',
          isSyntheticOrigin: true,
        };
        nodes.push(callerNode);
        if (primaryServer) {
          edges.push({
            from: callerNode,
            to: primaryServer,
            color: '#f5b82e',
            callId: callerNode.callId,
            dashed: true,
          });
        }
      }

      // Outgoing cross-entity call_sent pills (bottom lane right)
      group.crossEvents.forEach((ev, xIdx) => {
        const isErr = ev.result && ev.result !== 'success';
        const nx = group.crossEvents.length === 1
          ? rightX
          : centerX + (xIdx - (group.crossEvents.length - 1) / 2) * 145;
        const xNode = {
          id: `x-out-${idx}-${xIdx}`,
          ev,
          x: nx,
          y: crossY + (xIdx % 2 === 1 ? 26 : 0),
          lane: 'cross',
          variant: isErr ? 'error' : 'cross-out',
          title: `→ ${shortType(ev.targetEntity || '?')}`,
          subtitle: `${ev.method || ev.callKind || 'call'} (${formatMs(ev.durationMs)})`,
          callId: ev.callId || '',
        };
        nodes.push(xNode);
        if (primaryServer) {
          edges.push({
            from: primaryServer,
            to: xNode,
            color: isErr ? '#f87171' : '#f5b82e',
            callId: ev.callId || primaryServer.callId,
            dashed: true,
          });
        }
      });
    });

    const entityLabel = shortType(this.entityKey);

    // Build SVG layer (swimlane rails, sequence vertical dotted lines, dashed causal diagonals)
    const svgEdges = edges.map((edge, i) => `
      <line class="tl-edge" data-edge-idx="${i}" data-call-id="${escapeHtml(edge.callId)}"
            x1="${edge.from.x}" y1="${edge.from.y}" x2="${edge.to.x}" y2="${edge.to.y}"
            stroke="${edge.color}" stroke-width="2.2"
            ${edge.dashed ? 'stroke-dasharray="6 5"' : ''}
            stroke-linecap="round" opacity="0.82" />
    `).join('');

    const svgSeqLines = seqLines.map(s => `
      <g class="tl-seq-col" data-col-idx="${s.idx}">
        <line x1="${s.x}" y1="22" x2="${s.x}" y2="${totalHeight - 18}"
              stroke="rgba(99, 197, 240, 0.22)" stroke-width="1.5" stroke-dasharray="3 5" />
        <rect x="${s.x + 6}" y="12" width="48" height="20" rx="10"
              fill="${s.hasState ? 'rgba(245, 184, 46, 0.16)' : 'rgba(99, 197, 240, 0.10)'}"
              stroke="${s.hasState ? 'rgba(245, 184, 46, 0.5)' : 'rgba(99, 197, 240, 0.25)'}" stroke-width="1" />
        <text x="${s.x + 30}" y="25.5" text-anchor="middle"
              fill="${s.hasState ? '#f5b82e' : '#8ecae6'}" font-size="11" font-family="var(--font-mono)" font-weight="600">#${s.seq}</text>
      </g>
    `).join('');

    const railLines = `
      <line x1="194" y1="${clientY}" x2="${totalWidth - 20}" y2="${clientY}" stroke="#2a5878" stroke-width="2.5" stroke-linecap="round" />
      <line x1="194" y1="${serverY}" x2="${totalWidth - 20}" y2="${serverY}" stroke="#2a5878" stroke-width="2.5" stroke-linecap="round" />
      ${hasCross ? `<line x1="194" y1="${crossY}" x2="${totalWidth - 20}" y2="${crossY}" stroke="#2a5878" stroke-width="2" stroke-dasharray="4 4" stroke-linecap="round" />` : ''}
    `;

    // Build interactive HTML pill nodes positioned over SVG
    const pillsHtml = nodes.map(n => {
      const uid = eventUid(n.ev);
      const stateDot = n.hasState ? `<span class="pill-state-dot" title="Full state snapshot captured"></span>` : '';
      return `
        <button type="button"
                class="tl-pill pill-${n.variant}"
                data-event-uid="${escapeHtml(uid)}"
                data-call-id="${escapeHtml(n.callId)}"
                style="left:${n.x}px; top:${n.y}px;"
                title="${escapeHtml(n.title + (n.subtitle ? ' — ' + n.subtitle : ''))}">
          <span class="tl-pill-title">${stateDot}${escapeHtml(n.title)}</span>
          ${n.subtitle ? `<span class="tl-pill-sub">${escapeHtml(n.subtitle)}</span>` : ''}
        </button>
      `;
    }).join('');

    el.innerHTML = `
      <div class="tl-viewport" id="tl-viewport">
        <div class="tl-stage" style="width:${totalWidth}px; height:${totalHeight}px;">
          <div class="tl-lane-labels">
            <div class="tl-lane-tag tl-lane-client" style="top:${clientY - 22}px;">
              <span class="tl-lane-name">CLIENT TIMELINE</span>
              <span class="tl-lane-hint">RPC · Replies · Broadcasts</span>
            </div>
            <div class="tl-lane-tag tl-lane-server" style="top:${serverY - 22}px;">
              <span class="tl-lane-name">SERVER TIMELINE (ORLEANS)</span>
              <span class="tl-lane-hint" title="${escapeHtml(this.entityKey)}">${escapeHtml(entityLabel)}</span>
            </div>
            ${hasCross ? `
            <div class="tl-lane-tag tl-lane-cross" style="top:${crossY - 22}px;">
              <span class="tl-lane-name">CROSS-ENTITY</span>
              <span class="tl-lane-hint">Incoming &amp; Outgoing Hops</span>
            </div>` : ''}
          </div>
          <svg class="tl-svg" width="${totalWidth}" height="${totalHeight}" viewBox="0 0 ${totalWidth} ${totalHeight}">
            ${railLines}
            ${svgSeqLines}
            ${svgEdges}
          </svg>
          <div class="tl-nodes">${pillsHtml}</div>
        </div>
      </div>
    `;

    // Bind pill clicks
    el.querySelectorAll('.tl-pill').forEach(btn => {
      btn.addEventListener('click', () => {
        const uid = btn.getAttribute('data-event-uid');
        const ev = this.events.find(e => eventUid(e) === uid);
        if (ev) this.selectEvent(ev);
      });
    });

    // Enable click-drag horizontal panning on the viewport
    const viewport = el.querySelector('#tl-viewport');
    if (viewport) {
      viewport.addEventListener('mousedown', (e) => {
        if (e.target.closest('button')) return;
        this._dragPan = { startX: e.clientX, scrollLeft: viewport.scrollLeft };
        viewport.classList.add('is-panning');
      });
      const stopPan = () => {
        this._dragPan = null;
        viewport.classList.remove('is-panning');
      };
      viewport.addEventListener('mousemove', (e) => {
        if (!this._dragPan) return;
        viewport.scrollLeft = this._dragPan.scrollLeft - (e.clientX - this._dragPan.startX);
      });
      viewport.addEventListener('mouseup', stopPan);
      viewport.addEventListener('mouseleave', stopPan);
    }

    this._applySelectionStyles();
  }

  _applySelectionStyles() {
    const selected = this._getSelectedEvent();
    const selUid = selected ? eventUid(selected) : null;
    const selCallId = selected?.callId || null;

    this.canvasEl.querySelectorAll('.tl-pill').forEach(pill => {
      const uid = pill.getAttribute('data-event-uid');
      const cid = pill.getAttribute('data-call-id');
      const isSelected = uid === selUid;
      const isSameCall = selCallId && cid && cid === selCallId;
      pill.classList.toggle('is-selected', isSelected);
      pill.classList.toggle('is-related', !isSelected && Boolean(isSameCall));
    });

    this.canvasEl.querySelectorAll('.tl-edge').forEach(edge => {
      const cid = edge.getAttribute('data-call-id');
      const active = selCallId && cid && cid === selCallId;
      edge.classList.toggle('is-active', Boolean(active));
      edge.setAttribute('opacity', active ? '1' : selCallId ? '0.32' : '0.82');
      edge.setAttribute('stroke-width', active ? '3.2' : '2.2');
    });
  }

  _renderScrubber() {
    const el = this.scrubberEl;
    if (!el) return;
    if (!this.groups.length) {
      el.innerHTML = '';
      return;
    }
    const activeIdx = this.selectedEventKey
      ? this.groups.findIndex(g => g.allEvents.some(e => eventUid(e) === this.selectedEventKey))
      : -1;

    el.innerHTML = `
      <div class="scrubber-bar">
        <span class="scrubber-label">TIMEMACHINE SEQ</span>
        <button type="button" class="btn btn-xs" id="scrub-prev" ${activeIdx <= 0 ? 'disabled' : ''}>◀ Prev</button>
        <div class="scrubber-pills">
          ${this.groups.map(g => {
            const subTag = g.serverEvents.length === 0 && g.clientSubEvents.length > 0 ? ' · sub' : '';
            return `
            <button type="button" class="scrub-pill ${g.index === activeIdx ? 'is-active' : ''} ${g.hasError ? 'has-error' : ''} ${g.hasState ? 'has-state' : ''}"
                    data-group-idx="${g.index}" title="Column ${g.index + 1} · Sequence #${g.seq}${g.hasState ? ' (Full State)' : ''}">
              #${g.seq}${subTag}
            </button>
          `;
          }).join('')}
        </div>
        <button type="button" class="btn btn-xs" id="scrub-next" ${activeIdx < 0 || activeIdx >= this.groups.length - 1 ? 'disabled' : ''}>Next ▶</button>
      </div>
    `;

    el.querySelectorAll('.scrub-pill').forEach(btn => {
      btn.addEventListener('click', () => this.selectGroupIndex(Number(btn.dataset.groupIdx)));
    });
    el.querySelector('#scrub-prev')?.addEventListener('click', () => {
      if (activeIdx > 0) this.selectGroupIndex(activeIdx - 1);
    });
    el.querySelector('#scrub-next')?.addEventListener('click', () => {
      if (activeIdx >= 0 && activeIdx < this.groups.length - 1) this.selectGroupIndex(activeIdx + 1);
    });
  }

  _renderCard() {
    const el = this.cardEl;
    const ev = this._getSelectedEvent();
    if (!ev) {
      el.innerHTML = `
        <div class="shared-card">
          <div class="shared-card-header">
            <span class="shared-card-kicker">SHARED C# · INSPECTOR</span>
          </div>
          <div class="shared-card-body shared-card-empty">
            Click any pill on the timeline to inspect its arguments, result, state snapshot, and diff.
          </div>
        </div>
      `;
      return;
    }

    const headerTitle = ev.service && ev.method
      ? `${ev.service}.${ev.method}`
      : ev.callKind === 'init'
        ? `[MetaInit] v${ev.fromVersion ?? 0} → v${ev.toVersion ?? 0}`
        : ev.kind;

    const isErr = ev.result && ev.result !== 'success';
    const argPreview = buildSignaturePreview(ev);

    const otherEntity = ev.kind === 'call_sent' ? ev.targetEntity : ev.callerEntity;

    el.innerHTML = `
      <div class="shared-card ${isErr ? 'shared-card-error' : ''}">
        <div class="shared-card-header">
          <div class="shared-card-header-top">
            <span class="shared-card-kicker">${escapeHtml((ev.callKind || ev.kind).toUpperCase())}</span>
            <span class="shared-card-seq">#${ev.entitySequence}.${ev.subOrder}</span>
          </div>
          <div class="shared-card-title" title="${escapeHtml(headerTitle)}">${escapeHtml(headerTitle)}</div>
        </div>
        <div class="shared-card-body">
          <div class="code-sig-box">${escapeHtml(argPreview)}</div>
          <div class="card-badges">
            <span class="badge ${isErr ? 'badge-error' : 'badge-ok'}">${isErr ? '✗ ' + escapeHtml(ev.result) : '✓ ' + escapeHtml(ev.result || 'ok')}</span>
            <span class="badge badge-neutral">${formatMs(ev.durationMs)}</span>
            ${ev.silo ? `<span class="badge badge-neutral" title="Orleans Silo">${escapeHtml(ev.silo)}</span>` : ''}
            ${ev.callerId ? `<span class="badge badge-cyan" title="Caller / Player">player:${escapeHtml(ev.callerId)}</span>` : ''}
          </div>
          ${ev.error ? `<div class="card-error-box"><strong>${escapeHtml(ev.errorKind || 'Error')}:</strong> ${escapeHtml(ev.error)}</div>` : ''}
          <div class="card-actions">
            ${ev.callId ? `<button type="button" class="btn btn-xs btn-cyan" id="card-open-call">Call Tree</button>` : ''}
            ${otherEntity ? `<button type="button" class="btn btn-xs btn-amber" id="card-jump-entity">Jump → ${escapeHtml(shortType(otherEntity))}</button>` : ''}
            <button type="button" class="btn btn-xs" id="card-fetch-state-at">State @ #${ev.entitySequence}</button>
          </div>
          <div class="card-tabs">
            <button type="button" class="card-tab ${this.activeCardTab === 'payload' ? 'is-active' : ''}" data-tab="payload">Args / Result</button>
            <button type="button" class="card-tab ${this.activeCardTab === 'diff' ? 'is-active' : ''}" data-tab="diff">State Diff</button>
            <button type="button" class="card-tab ${this.activeCardTab === 'state' ? 'is-active' : ''}" data-tab="state">State</button>
            <button type="button" class="card-tab ${this.activeCardTab === 'meta' ? 'is-active' : ''}" data-tab="meta">Meta</button>
          </div>
          <div class="card-tab-pane" id="card-tab-pane"></div>
        </div>
      </div>
    `;

    el.querySelectorAll('.card-tab').forEach(btn => {
      btn.addEventListener('click', () => {
        this.activeCardTab = btn.dataset.tab;
        this._renderCard();
      });
    });

    el.querySelector('#card-open-call')?.addEventListener('click', () => {
      if (this.callbacks.onOpenCallTree && ev.callId) this.callbacks.onOpenCallTree(ev.callId);
    });
    el.querySelector('#card-jump-entity')?.addEventListener('click', () => {
      if (this.callbacks.onOpenEntity && otherEntity) {
        const { stateType, entityId } = splitEntityKey(otherEntity);
        this.callbacks.onOpenEntity(stateType, entityId);
      }
    });
    el.querySelector('#card-fetch-state-at')?.addEventListener('click', async () => {
      this.activeCardTab = 'state';
      await this._loadStateAt(ev.entitySequence);
      this._renderCard();
    });

    this._renderCardTabPane(ev);
  }

  async _loadStateAt(seq) {
    if (!this.callbacks.fetchStateAt || !this.entityKey) return null;
    if (this._stateCache.has(seq)) return this._stateCache.get(seq);
    const { stateType, entityId } = splitEntityKey(this.entityKey);
    try {
      const res = await this.callbacks.fetchStateAt(stateType, entityId, seq);
      if (res) {
        this._stateCache.set(seq, res);
        return res;
      }
    } catch {
      // handled silently in pane
    }
    return null;
  }

  _renderCardTabPane(ev) {
    const pane = this.cardEl.querySelector('#card-tab-pane');
    if (!pane) return;

    if (this.activeCardTab === 'payload') {
      pane.innerHTML = `
        <div class="kv-section">
          <div class="kv-title">Arguments (wire)</div>
          <div class="kv-body" id="card-args-tree"></div>
        </div>
        <div class="kv-section">
          <div class="kv-title">Result Value</div>
          <div class="kv-body" id="card-result-tree"></div>
        </div>
      `;
      renderJsonTree(pane.querySelector('#card-args-tree'), ev.args, { maxDepthExpand: 3, emptyText: '(none / not captured at Args+)' });
      renderJsonTree(pane.querySelector('#card-result-tree'), ev.resultValue, { maxDepthExpand: 3, emptyText: '(void / not captured at ArgsResult+)' });
      return;
    }

    if (this.activeCardTab === 'state') {
      const cached = ev.state !== undefined && ev.state !== null
        ? { state: ev.state, actualSeq: ev.entitySequence, isExact: true }
        : this._stateCache.get(ev.entitySequence);

      if (!cached) {
        pane.innerHTML = `
          <div class="state-hint">
            No inline state on this event.
            <button type="button" class="btn btn-xs btn-amber" id="btn-load-state-now">Fetch entity_state_at(#${ev.entitySequence})</button>
          </div>
        `;
        pane.querySelector('#btn-load-state-now')?.addEventListener('click', async () => {
          pane.innerHTML = `<div class="state-hint">Loading state at #${ev.entitySequence}…</div>`;
          await this._loadStateAt(ev.entitySequence);
          this._renderCardTabPane(ev);
        });
        return;
      }

      pane.innerHTML = `
        <div class="state-meta-row">
          <span>Snapshot @ <strong>#${cached.actualSeq ?? '—'}</strong></span>
          <span class="badge ${cached.isExact ? 'badge-ok' : 'badge-amber'}">${cached.isExact ? 'exact' : 'nearest earlier'}</span>
        </div>
        ${cached.note ? `<div class="state-hint">${escapeHtml(cached.note)}</div>` : ''}
        <div class="kv-body" id="card-state-tree"></div>
      `;
      renderJsonTree(pane.querySelector('#card-state-tree'), cached.state, { maxDepthExpand: 2, emptyText: '(no state)' });
      return;
    }

    if (this.activeCardTab === 'diff') {
      const curState = ev.state ?? this._stateCache.get(ev.entitySequence)?.state;
      // Find nearest earlier sequence with captured state
      let prevSeq = null;
      let prevState = null;
      for (const [s, info] of [...this._stateCache.entries()].sort((a, b) => a[0] - b[0])) {
        if (s < ev.entitySequence && info.state !== null && info.state !== undefined) {
          prevSeq = s;
          prevState = info.state;
        }
      }
      pane.innerHTML = `
        <div class="state-meta-row">
          <span>Comparing <strong>${prevSeq !== null ? '#' + prevSeq : '—'}</strong> → <strong>#${ev.entitySequence}</strong></span>
          ${prevSeq === null && ev.entitySequence > 0 ? `<button type="button" class="btn btn-xs" id="btn-fetch-prev-diff">Load #${ev.entitySequence - 1}</button>` : ''}
        </div>
        <div id="card-diff-body"></div>
      `;
      renderDiffList(pane.querySelector('#card-diff-body'), prevState, curState);
      pane.querySelector('#btn-fetch-prev-diff')?.addEventListener('click', async () => {
        await this._loadStateAt(ev.entitySequence - 1);
        if (curState === undefined) await this._loadStateAt(ev.entitySequence);
        this._renderCardTabPane(ev);
      });
      return;
    }

    // Meta tab
    const metaRows = [
      ['Kind', ev.kind],
      ['CallKind', ev.callKind],
      ['Entity', ev.entityKey],
      ['Sequence', `#${ev.entitySequence} (sub ${ev.subOrder})`],
      ['CallId', ev.callId],
      ['SpanId', ev.spanId],
      ['ParentSpanId', ev.parentSpanId],
      ['StartedAt', formatTime(ev.startedAtUtc)],
      ['Duration', formatMs(ev.durationMs)],
      ['Silo', ev.silo],
      ['CallerId', ev.callerId],
      ['CallerEntity', ev.callerEntity],
      ['TargetEntity', ev.targetEntity],
      ['TargetPlayer', ev.targetPlayer],
      ['Subscribers', ev.subscriberCount != null ? `${ev.subscriberCount} (${(ev.targetPlayers || []).join(', ')})` : null],
      ['SessionId', ev.sessionId],
      ['ClientVersion', ev.clientVersion],
      ['Migration', ev.fromVersion != null ? `v${ev.fromVersion} → v${ev.toVersion}` : null],
      ['Reason', ev.reason],
    ].filter(([, v]) => v !== null && v !== undefined && v !== '');

    pane.innerHTML = `
      <table class="meta-kv-table">
        ${metaRows.map(([k, v]) => `<tr><th>${escapeHtml(k)}</th><td><code>${escapeHtml(v)}</code></td></tr>`).join('')}
      </table>
    `;
  }
}

function eventUid(e) {
  return `${e.id ?? ''}|${e.kind}|${e.entitySequence}|${e.subOrder}|${e.spanId ?? ''}|${e.startedAtUtc ?? ''}`;
}

function groupEventsIntoColumns(events) {
  const bySeq = new Map();
  const order = [];

  for (const ev of events) {
    const seq = ev.entitySequence ?? 0;
    // Separate standalone subscription events at seq 0 from each other
    const key = (ev.kind === 'subscribed' || ev.kind === 'reclaimed' || ev.kind === 'unsubscribed')
      ? `sub:${ev.id}:${seq}`
      : `seq:${seq}`;
    if (!bySeq.has(key)) {
      const g = {
        index: order.length,
        seq,
        allEvents: [],
        serverEvents: [],
        clientOutEvents: [],
        clientSubEvents: [],
        crossEvents: [],
        rpcOrigin: null,
        incomingCross: null,
        hasState: false,
        hasError: false,
      };
      bySeq.set(key, g);
      order.push(g);
    }
    const g = bySeq.get(key);
    g.allEvents.push(ev);
    if (ev.state !== undefined && ev.state !== null) g.hasState = true;
    if (ev.result && ev.result !== 'success') g.hasError = true;

    switch (ev.kind) {
      case 'call_completed':
        g.serverEvents.push(ev);
        if (ev.callKind === 'rpc' && !g.rpcOrigin) g.rpcOrigin = ev;
        if (ev.callerEntity && !g.incomingCross) g.incomingCross = ev;
        break;
      case 'call_sent':
        g.crossEvents.push(ev);
        break;
      case 'client_responded':
      case 'broadcast_sent':
        g.clientOutEvents.push(ev);
        break;
      case 'subscribed':
      case 'reclaimed':
      case 'unsubscribed':
        g.clientSubEvents.push(ev);
        break;
      default:
        g.serverEvents.push(ev);
        break;
    }
  }

  return order;
}

function classifyServerVariant(ev) {
  if (ev.result && ev.result !== 'success') return 'error';
  if (ev.callKind === 'trigger' || ev.callKind === 'nested') return 'trigger';
  if (ev.callKind === 'init') return 'init';
  if (ev.callKind === 'query') return 'query';
  return 'server';
}

function serverPillTitle(ev) {
  if (ev.callKind === 'init') return `[MetaInit] v${ev.fromVersion ?? 0}→v${ev.toVersion ?? 0}`;
  const prefix = ev.callKind === 'trigger' ? '⚡ ' : ev.callKind === 'query' ? '? ' : ev.callKind === 'signal' ? '◉ ' : '';
  return prefix + (ev.method || ev.kind);
}

function serverPillSubtitle(ev, zoom) {
  const parts = [];
  if (zoom === 'detailed' && ev.service) parts.push(ev.service);
  if (ev.durationMs != null) parts.push(formatMs(ev.durationMs));
  if (ev.result && ev.result !== 'success') parts.push(`✗ ${ev.result}`);
  return parts.join(' · ');
}

function clientCallTitle(ev) {
  return `${ev.method || 'RPC'} (client)`;
}

function clientOutTitle(ev) {
  if (ev.kind === 'broadcast_sent') {
    const n = ev.subscriberCount ?? (ev.targetPlayers ? ev.targetPlayers.length : 1);
    return `📡 Broadcast ×${n}`;
  }
  if (ev.result && ev.result !== 'success') return `✗ ${ev.result}`;
  return `✓ Verified & Synced`;
}

function clientOutSubtitle(ev) {
  if (ev.kind === 'broadcast_sent') return ev.method || 'subscribers';
  const who = ev.callerId ? `player:${ev.callerId}` : '';
  const dur = ev.durationMs != null ? formatMs(ev.durationMs) : '';
  return [who, dur].filter(Boolean).join(' · ');
}

function formatSubKind(kind) {
  if (kind === 'subscribed') return '+ Subscribed';
  if (kind === 'reclaimed') return '↻ Reclaimed';
  if (kind === 'unsubscribed') return '− Unsubscribed';
  return kind;
}

function buildSignaturePreview(ev) {
  if (ev.callKind === 'init') {
    return `[MetaInit] Migration v${ev.fromVersion ?? 0} → v${ev.toVersion ?? 0}`;
  }
  const name = ev.method || ev.kind;
  if (ev.args && typeof ev.args === 'object' && !Array.isArray(ev.args)) {
    const entries = Object.entries(ev.args).map(([k, v]) => {
      const val = typeof v === 'string' ? JSON.stringify(v) : JSON.stringify(v);
      const shortVal = val && val.length > 26 ? val.slice(0, 23) + '…' : val;
      return `${k}: ${shortVal}`;
    });
    return `${name}(${entries.join(', ')})`;
  }
  return `${name}()`;
}
