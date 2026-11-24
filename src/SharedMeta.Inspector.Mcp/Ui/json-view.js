// Interactive collapsible JSON tree viewer & structural JSON diff engine.

export function escapeHtml(str) {
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

export function shortType(fullName) {
  if (!fullName) return '';
  if (fullName.startsWith('player:')) return fullName;
  const colon = fullName.indexOf(':');
  if (colon >= 0) {
    const type = fullName.slice(0, colon);
    const id = fullName.slice(colon + 1);
    const dot = Math.max(type.lastIndexOf('.'), type.lastIndexOf('+'));
    return (dot >= 0 ? type.slice(dot + 1) : type) + ':' + id;
  }
  const dot = Math.max(fullName.lastIndexOf('.'), fullName.lastIndexOf('+'));
  return dot >= 0 ? fullName.slice(dot + 1) : fullName;
}

export function splitEntityKey(key) {
  if (!key) return { stateType: '', entityId: '' };
  const idx = key.indexOf(':');
  if (idx <= 0) return { stateType: '', entityId: key };
  return { stateType: key.slice(0, idx), entityId: key.slice(idx + 1) };
}

export function formatMs(ms) {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return '—';
  if (ms < 0.01) return '<0.01 ms';
  if (ms < 10) return `${ms.toFixed(2)} ms`;
  if (ms < 1000) return `${ms.toFixed(1)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
}

export function formatTime(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return String(iso);
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  const ss = String(d.getSeconds()).padStart(2, '0');
  const ms = String(d.getMilliseconds()).padStart(3, '0');
  return `${hh}:${mm}:${ss}.${ms}`;
}

export function formatBytes(bytes) {
  if (bytes === null || bytes === undefined) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

/**
 * Renders a collapsible interactive JSON tree inside `container`.
 * Clicking a key triggers `onPathClick(path)` if provided (for live state path drilling).
 */
export function renderJsonTree(container, value, { maxDepthExpand = 3, onPathClick = null, emptyText = 'null' } = {}) {
  container.innerHTML = '';
  if (value === undefined) {
    container.innerHTML = `<span class="jv-null">${escapeHtml(emptyText)}</span>`;
    return;
  }
  const root = document.createElement('div');
  root.className = 'jv-root';
  root.appendChild(buildNode(value, null, '', 0, maxDepthExpand, onPathClick));
  container.appendChild(root);
}

function buildNode(value, key, path, depth, maxDepthExpand, onPathClick) {
  const row = document.createElement('div');
  row.className = 'jv-row';

  const isArray = Array.isArray(value);
  const isObject = value !== null && typeof value === 'object' && !isArray;

  if (!isArray && !isObject) {
    if (key !== null) row.appendChild(makeKeyEl(key, path, onPathClick));
    row.appendChild(makePrimitiveEl(value));
    return row;
  }

  const entries = isArray ? value.map((v, i) => [i, v]) : Object.entries(value);
  const openChar = isArray ? '[' : '{';
  const closeChar = isArray ? ']' : '}';

  if (entries.length === 0) {
    if (key !== null) row.appendChild(makeKeyEl(key, path, onPathClick));
    const empty = document.createElement('span');
    empty.className = 'jv-punct';
    empty.textContent = openChar + closeChar;
    row.appendChild(empty);
    return row;
  }

  let expanded = depth < maxDepthExpand;
  const header = document.createElement('div');
  header.className = 'jv-branch-head';

  const toggle = document.createElement('button');
  toggle.type = 'button';
  toggle.className = 'jv-toggle';
  toggle.textContent = expanded ? '▾' : '▸';
  header.appendChild(toggle);

  if (key !== null) header.appendChild(makeKeyEl(key, path, onPathClick));

  const summary = document.createElement('span');
  summary.className = 'jv-summary';
  summary.textContent = expanded
    ? openChar
    : `${openChar} ${entries.length} ${isArray ? (entries.length === 1 ? 'item' : 'items') : (entries.length === 1 ? 'key' : 'keys')} ${closeChar}`;
  header.appendChild(summary);
  row.appendChild(header);

  const childrenEl = document.createElement('div');
  childrenEl.className = 'jv-children';
  childrenEl.hidden = !expanded;

  let rendered = false;
  const ensureRendered = () => {
    if (rendered) return;
    rendered = true;
    for (const [k, v] of entries) {
      const childPath = isArray
        ? (path ? `${path}[${k}]` : `[${k}]`)
        : (path ? `${path}.${k}` : String(k));
      childrenEl.appendChild(buildNode(v, isArray ? `[${k}]` : k, childPath, depth + 1, maxDepthExpand, onPathClick));
    }
    const closing = document.createElement('div');
    closing.className = 'jv-close';
    closing.textContent = closeChar;
    childrenEl.appendChild(closing);
  };

  if (expanded) ensureRendered();

  const flip = () => {
    expanded = !expanded;
    if (expanded) ensureRendered();
    toggle.textContent = expanded ? '▾' : '▸';
    childrenEl.hidden = !expanded;
    summary.textContent = expanded
      ? openChar
      : `${openChar} ${entries.length} ${isArray ? (entries.length === 1 ? 'item' : 'items') : (entries.length === 1 ? 'key' : 'keys')} ${closeChar}`;
  };
  toggle.addEventListener('click', flip);
  summary.addEventListener('click', flip);

  row.appendChild(childrenEl);
  return row;
}

function makeKeyEl(key, path, onPathClick) {
  const span = document.createElement('span');
  span.className = onPathClick ? 'jv-key jv-key-clickable' : 'jv-key';
  span.textContent = String(key);
  if (onPathClick && path) {
    span.title = `Click to query path: ${path}`;
    span.addEventListener('click', (e) => {
      e.stopPropagation();
      onPathClick(path);
    });
  }
  const wrap = document.createDocumentFragment();
  wrap.appendChild(span);
  const colon = document.createElement('span');
  colon.className = 'jv-punct';
  colon.textContent = ': ';
  wrap.appendChild(colon);
  return wrap;
}

function makePrimitiveEl(value) {
  const span = document.createElement('span');
  if (value === null) {
    span.className = 'jv-null';
    span.textContent = 'null';
  } else if (typeof value === 'boolean') {
    span.className = 'jv-bool';
    span.textContent = String(value);
  } else if (typeof value === 'number') {
    span.className = 'jv-num';
    span.textContent = String(value);
  } else if (typeof value === 'string') {
    span.className = value === '***' ? 'jv-redacted' : 'jv-str';
    span.textContent = JSON.stringify(value);
  } else {
    span.className = 'jv-null';
    span.textContent = String(value);
  }
  return span;
}

/**
 * Computes a flat list of structural changes between `before` and `after` JSON values.
 * Returns [{ path, kind: 'added' | 'removed' | 'changed', before, after }]
 */
export function computeJsonDiff(before, after, prefix = '', out = []) {
  if (Object.is(before, after)) return out;
  if (before === null || after === null || typeof before !== 'object' || typeof after !== 'object'
      || Array.isArray(before) !== Array.isArray(after)) {
    out.push({
      path: prefix || '(root)',
      kind: before === undefined ? 'added' : after === undefined ? 'removed' : 'changed',
      before,
      after,
    });
    return out;
  }

  if (Array.isArray(before)) {
    const maxLen = Math.max(before.length, after.length);
    for (let i = 0; i < maxLen; i++) {
      const p = prefix ? `${prefix}[${i}]` : `[${i}]`;
      if (i >= before.length) out.push({ path: p, kind: 'added', before: undefined, after: after[i] });
      else if (i >= after.length) out.push({ path: p, kind: 'removed', before: before[i], after: undefined });
      else computeJsonDiff(before[i], after[i], p, out);
    }
    return out;
  }

  const keys = new Set([...Object.keys(before), ...Object.keys(after)]);
  for (const k of keys) {
    const p = prefix ? `${prefix}.${k}` : k;
    const inBefore = Object.prototype.hasOwnProperty.call(before, k);
    const inAfter = Object.prototype.hasOwnProperty.call(after, k);
    if (!inBefore && inAfter) out.push({ path: p, kind: 'added', before: undefined, after: after[k] });
    else if (inBefore && !inAfter) out.push({ path: p, kind: 'removed', before: before[k], after: undefined });
    else computeJsonDiff(before[k], after[k], p, out);
  }
  return out;
}

export function renderDiffList(container, before, after) {
  container.innerHTML = '';
  if (before === undefined || before === null) {
    container.innerHTML = `<div class="diff-empty">No earlier state snapshot available to diff against.</div>`;
    return;
  }
  if (after === undefined || after === null) {
    container.innerHTML = `<div class="diff-empty">No state snapshot captured for this sequence. Capture at level <code>Full</code>.</div>`;
    return;
  }
  const changes = computeJsonDiff(before, after);
  if (changes.length === 0) {
    container.innerHTML = `<div class="diff-empty diff-same">No state changes between snapshots.</div>`;
    return;
  }

  const list = document.createElement('div');
  list.className = 'diff-list';
  for (const c of changes) {
    const item = document.createElement('div');
    item.className = `diff-item diff-${c.kind}`;
    const symbol = c.kind === 'added' ? '+' : c.kind === 'removed' ? '−' : '~';
    const beforeTxt = c.before !== undefined ? compactValue(c.before) : '';
    const afterTxt = c.after !== undefined ? compactValue(c.after) : '';
    item.innerHTML = `
      <span class="diff-sign">${symbol}</span>
      <span class="diff-path">${escapeHtml(c.path)}</span>
      <span class="diff-vals">
        ${c.kind !== 'added' ? `<span class="diff-old">${escapeHtml(beforeTxt)}</span>` : ''}
        ${c.kind === 'changed' ? `<span class="diff-arrow">→</span>` : ''}
        ${c.kind !== 'removed' ? `<span class="diff-new">${escapeHtml(afterTxt)}</span>` : ''}
      </span>
    `;
    list.appendChild(item);
  }
  container.appendChild(list);
}

function compactValue(v) {
  if (v === null) return 'null';
  if (typeof v === 'string') return JSON.stringify(v);
  if (typeof v !== 'object') return String(v);
  const s = JSON.stringify(v);
  return s.length > 110 ? s.slice(0, 107) + '…' : s;
}
