import { escapeHtml, shortType, splitEntityKey } from './json-view.js';

/**
 * Interactive SVG Dependency Graph renderer for `entity_graph`.
 * Layouts players on the left, focal entity in the center, and connected entities
 * in rings/columns with draggable nodes and curved labeled edges.
 */
export class GraphView {
  constructor({ canvasEl, detailEl, callbacks }) {
    this.canvasEl = canvasEl;
    this.detailEl = detailEl;
    this.callbacks = callbacks;

    this.data = null;
    this.positions = new Map();
    this.selectedNode = null;
    this._drag = null;
  }

  setData(graphData) {
    this.data = graphData;
    this.selectedNode = graphData?.center || null;
    this._computeInitialLayout();
    this.render();
  }

  _computeInitialLayout() {
    this.positions.clear();
    if (!this.data || !Array.isArray(this.data.nodes)) return;

    const width = Math.max(780, this.canvasEl.clientWidth || 860);
    const height = Math.max(440, this.canvasEl.clientHeight || 480);
    const center = this.data.center;
    const players = [];
    const entities = [];
    for (const n of this.data.nodes) {
      if (n === center) continue;
      if (n.startsWith('player:')) players.push(n);
      else entities.push(n);
    }

    const cx = players.length === 0 && entities.length > 0 ? width * 0.27
      : entities.length === 0 && players.length > 0 ? width * 0.73
      : width * 0.5;
    const cy = height * 0.5;

    if (center) this.positions.set(center, { x: cx, y: cy });

    const r = Math.max(330, Math.min(width * 0.44, 410));

    // Place players along left arc
    players.forEach((p, i) => {
      const span = Math.min(Math.PI * 0.75, Math.max(0.4, (players.length - 1) * 0.38));
      const angle = players.length === 1 ? Math.PI : Math.PI - span / 2 + (span * i) / (players.length - 1);
      this.positions.set(p, { x: Math.max(120, cx + Math.cos(angle) * r), y: cy + Math.sin(angle) * Math.min(180, r * 0.65) });
    });

    // Place other entities along right arc
    entities.forEach((e, i) => {
      const span = Math.min(Math.PI * 0.85, Math.max(0.45, (entities.length - 1) * 0.42));
      const angle = entities.length === 1 ? 0 : -span / 2 + (span * i) / (entities.length - 1);
      this.positions.set(e, { x: Math.min(width - 120, cx + Math.cos(angle) * r), y: cy + Math.sin(angle) * Math.min(180, r * 0.72) });
    });
  }

  render() {
    const el = this.canvasEl;
    if (!this.data || !this.data.nodes || this.data.nodes.length === 0) {
      el.innerHTML = `
        <div class="tl-empty">
          <h3>No dependency graph loaded</h3>
          <p>Choose an entity above to map all players, cross-entity calls, and broadcasts within <code>depth</code> hops.</p>
        </div>
      `;
      this._renderDetail();
      return;
    }

    const width = Math.max(760, el.clientWidth || 860);
    const height = Math.max(460, el.clientHeight || 500);
    const edges = this.data.edges || [];

    // Detect bidirectional edge pairs to curve them apart
    const pairSet = new Set(edges.map(e => `${e.from}|${e.to}`));

    const defs = `
      <defs>
        <marker id="arr-cyan" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M 0 1 L 10 5 L 0 9 z" fill="#5bc0eb" />
        </marker>
        <marker id="arr-amber" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M 0 1 L 10 5 L 0 9 z" fill="#f5b82e" />
        </marker>
        <marker id="arr-green" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M 0 1 L 10 5 L 0 9 z" fill="#34d399" />
        </marker>
      </defs>
    `;

    const edgesSvg = edges.map(edge => {
      const p1 = this.positions.get(edge.from);
      const p2 = this.positions.get(edge.to);
      if (!p1 || !p2) return '';

      const dx = p2.x - p1.x;
      const dy = p2.y - p1.y;
      const dist = Math.hypot(dx, dy) || 1;
      const ux = dx / dist;
      const uy = dy / dist;

      // Trim start/end cleanly near node boundary
      const padStart = 112;
      const padEnd = 118;
      const sx = p1.x + ux * Math.min(padStart, dist * 0.34);
      const sy = p1.y + uy * Math.min(28, dist * 0.34);
      const ex = p2.x - ux * Math.min(padEnd, dist * 0.34);
      const ey = p2.y - uy * Math.min(28, dist * 0.34);

      const hasReverse = pairSet.has(`${edge.to}|${edge.from}`);
      const curveOffset = hasReverse ? 42 : 0;
      const mx = (sx + ex) / 2 - uy * curveOffset;
      const my = (sy + ey) / 2 + ux * curveOffset;
      const labelY = hasReverse ? my : my - 18;

      const isBroadcast = (edge.methods || []).every(m => m.startsWith('broadcast '));
      const isFromPlayer = edge.from.startsWith('player:');
      const color = isBroadcast ? '#34d399' : isFromPlayer ? '#5bc0eb' : '#f5b82e';
      const marker = isBroadcast ? 'url(#arr-green)' : isFromPlayer ? 'url(#arr-cyan)' : 'url(#arr-amber)';
      const dash = isBroadcast ? 'stroke-dasharray="6 5"' : '';

      const methodsText = (edge.methods || []).slice(0, 2).map(m => m.replace(/^broadcast /, '📡 ')).join(', ')
        + ((edge.methods?.length || 0) > 2 ? '…' : '')
        + (edge.count > 1 ? ` ×${edge.count}` : '');

      const isHighlighted = !this.selectedNode || edge.from === this.selectedNode || edge.to === this.selectedNode;

      return `
        <g class="g-edge" opacity="${isHighlighted ? '0.92' : '0.28'}">
          <path d="M ${sx} ${sy} Q ${mx} ${my} ${ex} ${ey}"
                fill="none" stroke="${color}" stroke-width="${isHighlighted ? 2.3 : 1.7}" ${dash}
                marker-end="${marker}" />
          <g transform="translate(${mx}, ${labelY})">
            <rect x="-86" y="-11" width="172" height="22" rx="11" fill="#0d2335" stroke="${color}" stroke-opacity="0.5" stroke-width="1" />
            <text text-anchor="middle" y="4" fill="#e2f1fb" font-size="10.5" font-family="var(--font-mono)">
              ${escapeHtml(methodsText.length > 26 ? methodsText.slice(0, 24) + '…' : methodsText)}
            </text>
          </g>
        </g>
      `;
    }).join('');

    const nodesSvg = this.data.nodes.map(node => {
      const pos = this.positions.get(node) || { x: width / 2, y: height / 2 };
      const isPlayer = node.startsWith('player:');
      const isCenter = node === this.data.center;
      const isSelected = node === this.selectedNode;
      const label = shortType(node);
      const display = label.length > 24 ? label.slice(0, 22) + '…' : label;
      const w = Math.max(138, Math.min(220, display.length * 8.2 + 34));
      const h = 44;

      const fill = isPlayer ? '#1c4d6e' : isCenter ? '#234f6e' : '#153852';
      const stroke = isSelected ? '#ffffff' : isCenter ? '#f5b82e' : isPlayer ? '#5bc0eb' : '#4a83a8';
      const strokeW = isCenter || isSelected ? 2.6 : 1.6;
      const rx = isPlayer ? 22 : 12;

      return `
        <g class="g-node ${isCenter ? 'is-center' : ''} ${isSelected ? 'is-selected' : ''}"
           data-node="${escapeHtml(node)}" transform="translate(${pos.x}, ${pos.y})">
          ${isCenter ? `<rect x="${-w / 2 - 5}" y="${-h / 2 - 5}" width="${w + 10}" height="${h + 10}" rx="${rx + 4}" fill="none" stroke="#f5b82e" stroke-opacity="0.28" stroke-width="4" />` : ''}
          <rect x="${-w / 2}" y="${-h / 2}" width="${w}" height="${h}" rx="${rx}"
                fill="${fill}" stroke="${stroke}" stroke-width="${strokeW}" />
          <text text-anchor="middle" y="-2" fill="#f8fafc" font-size="12" font-weight="600" font-family="var(--font-mono)">${escapeHtml(display)}</text>
          <text text-anchor="middle" y="13" fill="${isPlayer ? '#8ecae6' : '#f5b82e'}" font-size="9.5" font-weight="600" letter-spacing="0.06em">
            ${isPlayer ? 'PLAYER' : isCenter ? 'FOCAL ENTITY' : 'ENTITY'}
          </text>
        </g>
      `;
    }).join('');

    el.innerHTML = `
      <svg class="graph-svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">
        ${defs}
        ${edgesSvg}
        ${nodesSvg}
      </svg>
    `;

    const svg = el.querySelector('.graph-svg');
    if (svg) {
      svg.querySelectorAll('.g-node').forEach(g => {
        g.addEventListener('mousedown', (e) => {
          const node = g.getAttribute('data-node');
          this.selectedNode = node;
          const pos = this.positions.get(node);
          this._drag = { node, startX: e.clientX, startY: e.clientY, origX: pos.x, origY: pos.y, moved: false };
          e.stopPropagation();
        });
      });

      svg.addEventListener('mousemove', (e) => {
        if (!this._drag) return;
        const dx = e.clientX - this._drag.startX;
        const dy = e.clientY - this._drag.startY;
        if (Math.abs(dx) > 3 || Math.abs(dy) > 3) this._drag.moved = true;
        this.positions.set(this._drag.node, {
          x: Math.max(85, Math.min(width - 85, this._drag.origX + dx)),
          y: Math.max(36, Math.min(height - 36, this._drag.origY + dy)),
        });
        this.render();
      });

      const finish = () => {
        if (!this._drag) return;
        const wasMoved = this._drag.moved;
        this._drag = null;
        if (!wasMoved) this.render();
      };
      svg.addEventListener('mouseup', finish);
      svg.addEventListener('mouseleave', finish);
    }

    this._renderDetail();
  }

  _renderDetail() {
    const el = this.detailEl;
    if (!el) return;
    if (!this.data || !this.selectedNode) {
      el.innerHTML = `<div class="muted-note">Click a node in the graph to inspect its connections.</div>`;
      return;
    }

    const node = this.selectedNode;
    const isPlayer = node.startsWith('player:');
    const playerId = isPlayer ? node.slice(7) : '';
    const { stateType, entityId } = isPlayer ? { stateType: '', entityId: '' } : splitEntityKey(node);
    const incoming = (this.data.edges || []).filter(e => e.to === node);
    const outgoing = (this.data.edges || []).filter(e => e.from === node);

    el.innerHTML = `
      <div class="shared-card">
        <div class="shared-card-header">
          <div class="shared-card-header-top">
            <span class="shared-card-kicker">${isPlayer ? 'PLAYER NODE' : 'ENTITY NODE'}</span>
            <span class="shared-card-seq">${this.data.eventsScanned ?? 0} scanned</span>
          </div>
          <div class="shared-card-title">${escapeHtml(shortType(node))}</div>
        </div>
        <div class="shared-card-body">
          <div class="card-actions">
            ${!isPlayer ? `
              <button type="button" class="btn btn-xs btn-amber" id="g-open-tl">Timeline</button>
              <button type="button" class="btn btn-xs btn-cyan" id="g-recenter">Re-center</button>
              <button type="button" class="btn btn-xs" id="g-watch">Watch Full</button>
              <button type="button" class="btn btn-xs" id="g-live-state">Live State</button>
            ` : `
              <button type="button" class="btn btn-xs btn-cyan" id="g-watch-player">Watch Player</button>
              <button type="button" class="btn btn-xs" id="g-filter-player">Filter Calls</button>
            `}
          </div>
          <div class="kv-section">
            <div class="kv-title">Incoming (${incoming.length})</div>
            ${incoming.length === 0 ? '<div class="muted-note">None in window</div>' : incoming.map(e => `
              <div class="edge-list-row">
                <strong>${escapeHtml(shortType(e.from))}</strong>
                <span class="badge badge-neutral">×${e.count}</span>
                <div class="edge-methods">${escapeHtml((e.methods || []).join(', '))}</div>
              </div>
            `).join('')}
          </div>
          <div class="kv-section">
            <div class="kv-title">Outgoing (${outgoing.length})</div>
            ${outgoing.length === 0 ? '<div class="muted-note">None in window</div>' : outgoing.map(e => `
              <div class="edge-list-row">
                <strong>→ ${escapeHtml(shortType(e.to))}</strong>
                <span class="badge badge-neutral">×${e.count}</span>
                <div class="edge-methods">${escapeHtml((e.methods || []).join(', '))}</div>
              </div>
            `).join('')}
          </div>
        </div>
      </div>
    `;

    el.querySelector('#g-open-tl')?.addEventListener('click', () => this.callbacks.onOpenTimeline(stateType, entityId));
    el.querySelector('#g-recenter')?.addEventListener('click', () => this.callbacks.onRecenter(stateType, entityId));
    el.querySelector('#g-watch')?.addEventListener('click', () => this.callbacks.onWatchEntity(entityId));
    el.querySelector('#g-live-state')?.addEventListener('click', () => this.callbacks.onOpenLiveState(stateType, entityId));
    el.querySelector('#g-watch-player')?.addEventListener('click', () => this.callbacks.onWatchPlayer(playerId));
    el.querySelector('#g-filter-player')?.addEventListener('click', () => this.callbacks.onFilterPlayer(playerId));
  }
}
