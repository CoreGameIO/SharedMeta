// Stateless MCP Streamable HTTP JSON-RPC client for SharedMeta Inspector.
// Works directly against MapSharedMetaInspectorMcp ("/mcp" or any localhost MCP URL).

export class McpError extends Error {
  constructor(message, { status = 0, isAuthError = false, isToolError = false, raw = null } = {}) {
    super(message);
    this.name = 'McpError';
    this.status = status;
    this.isAuthError = isAuthError;
    this.isToolError = isToolError;
    this.raw = raw;
  }
}

export class McpClient {
  constructor(defaultUrl = '/mcp') {
    const savedUrl = sessionStorage.getItem('sharedmeta.inspector.url');
    this.url = savedUrl || defaultUrl;
    this.apiKey = sessionStorage.getItem('sharedmeta.inspector.apiKey') || '';
    this._nextId = 1;
    this.onAuthRequired = null;
  }

  setEndpoint(url) {
    this.url = (url || '/mcp').trim();
    sessionStorage.setItem('sharedmeta.inspector.url', this.url);
  }

  setApiKey(key) {
    this.apiKey = (key || '').trim();
    if (this.apiKey) sessionStorage.setItem('sharedmeta.inspector.apiKey', this.apiKey);
    else sessionStorage.removeItem('sharedmeta.inspector.apiKey');
  }

  async rpc(method, params = {}) {
    const id = this._nextId++;
    const headers = {
      'Content-Type': 'application/json',
      'Accept': 'application/json, text/event-stream',
    };
    if (this.apiKey) {
      headers['X-Inspector-Key'] = this.apiKey;
    }

    let response;
    try {
      response = await fetch(this.url, {
        method: 'POST',
        headers,
        body: JSON.stringify({ jsonrpc: '2.0', id, method, params }),
      });
    } catch (err) {
      throw new McpError(`Network error contacting ${this.url}: ${err.message}`, { raw: err });
    }

    const bodyText = await response.text();
    if (response.status === 401) {
      if (this.onAuthRequired) this.onAuthRequired(bodyText || 'API key required');
      throw new McpError(bodyText || 'Unauthorized (401): API key missing or wrong.', {
        status: 401,
        isAuthError: true,
      });
    }
    if (!response.ok) {
      throw new McpError(bodyText || `HTTP ${response.status} ${response.statusText}`, {
        status: response.status,
      });
    }

    const payload = parseMcpResponse(bodyText, id);
    if (!payload) {
      throw new McpError('Empty or unreadable MCP response.', { raw: bodyText });
    }
    if (payload.error) {
      throw new McpError(payload.error.message || JSON.stringify(payload.error), { raw: payload.error });
    }
    return payload.result;
  }

  async callTool(name, args = {}) {
    const cleaned = {};
    for (const [k, v] of Object.entries(args || {})) {
      if (v !== undefined && v !== null && v !== '') cleaned[k] = v;
    }
    const result = await this.rpc('tools/call', { name, arguments: cleaned });
    const text = extractTextContent(result);
    if (result?.isError) {
      throw new McpError(text || `Tool '${name}' failed.`, { isToolError: true, raw: result });
    }
    if (!text) return null;
    try {
      return JSON.parse(text);
    } catch {
      return { text };
    }
  }

  async listTools() {
    const result = await this.rpc('tools/list', {});
    return result?.tools || [];
  }

  // Typed convenience wrappers for all 17 Inspector MCP tools
  status() { return this.callTool('inspector_status'); }
  listMethods() { return this.callTool('list_methods'); }
  getCaptureConfig() { return this.callTool('get_capture_config'); }
  setCaptureConfig(cfg) {
    // Allow explicit empty object for methodLevels to clear overrides
    const args = {};
    if (cfg.level !== undefined) args.level = cfg.level;
    if (cfg.methodLevels !== undefined) args.methodLevels = cfg.methodLevels;
    if (cfg.sampleRate !== undefined) args.sampleRate = cfg.sampleRate;
    if (cfg.broadcastPerSubscriber !== undefined) args.broadcastPerSubscriber = cfg.broadcastPerSubscriber;
    return this.callTool('set_capture_config', args);
  }
  watchEntity(entityId, level = 'Full') { return this.callTool('watch_entity', { entityId, level }); }
  unwatchEntity(entityId) { return this.callTool('unwatch_entity', { entityId }); }
  watchPlayer(playerId, level = 'ArgsResult') { return this.callTool('watch_player', { playerId, level }); }
  unwatchPlayer(playerId) { return this.callTool('unwatch_player', { playerId }); }
  recentCalls(filter = {}) { return this.callTool('recent_calls', filter); }
  getCall(callId, includePayloads = true) { return this.callTool('get_call', { callId, includePayloads }); }
  entityTimeline(params) { return this.callTool('entity_timeline', params); }
  entityGraph(params) { return this.callTool('entity_graph', params); }
  entityStateAt(stateType, entityId, seq) { return this.callTool('entity_state_at', { stateType, entityId, seq }); }
  getEntityState(stateType, entityId, path, activate = false) {
    const args = { stateType, entityId, activate };
    if (path) args.path = path;
    return this.callTool('get_entity_state', args);
  }
  listEntities(stateType, limit = 200) { return this.callTool('list_entities', { stateType, limit }); }
  listSessions() { return this.callTool('list_sessions'); }
  invokeMethod(params) { return this.callTool('invoke_method', params); }
}

function parseMcpResponse(text, expectedId) {
  const trimmed = (text || '').trim();
  if (!trimmed) return null;
  if (trimmed.startsWith('{')) {
    return JSON.parse(trimmed);
  }
  // Streamable HTTP SSE framing: collect `data:` lines per event block
  let lastParsed = null;
  const blocks = trimmed.split(/\r?\n\r?\n/);
  for (const block of blocks) {
    const dataLines = [];
    for (const line of block.split(/\r?\n/)) {
      if (line.startsWith('data:')) {
        dataLines.push(line.slice(5).trimStart());
      }
    }
    if (dataLines.length === 0) continue;
    const joined = dataLines.join('\n').trim();
    if (!joined) continue;
    try {
      const obj = JSON.parse(joined);
      lastParsed = obj;
      if (obj.id === expectedId) return obj;
    } catch {
      // ignore keep-alive or non-JSON comment chunks
    }
  }
  return lastParsed;
}

function extractTextContent(result) {
  if (!result || !Array.isArray(result.content)) return '';
  return result.content
    .filter(c => c && c.type === 'text' && typeof c.text === 'string')
    .map(c => c.text)
    .join('\n');
}
