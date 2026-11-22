import { existsSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { coreEndpoint } from './coreClient.js';

export interface WebShellProfile {
  id: string;
  label: string;
  shell: string;
  args: string[];
}

export interface WsTerminalClient {
  send(data: string): void;
  close(code?: number, reason?: string): void;
}

export interface WsTerminalSession {
  onMessage(raw: string | Buffer | Uint8Array): void;
  close(): void;
}

interface ActivePipeShell {
  proc: ReturnType<typeof Bun.spawn>;
  closed: boolean;
}

export function detectHostShellProfiles(): WebShellProfile[] {
  if (process.platform === 'win32') {
    const profiles: WebShellProfile[] = [];
    const pwsh = Bun.which('pwsh');
    if (pwsh) {
      profiles.push({
        id: 'pwsh',
        label: 'PowerShell 7',
        shell: pwsh,
        args: ['-NoLogo', '-NoExit', '-Command', '-'],
      });
    }
    const powershell = Bun.which('powershell');
    if (powershell) {
      profiles.push({
        id: 'powershell',
        label: 'Windows PowerShell',
        shell: powershell,
        args: ['-NoLogo', '-NoExit', '-Command', '-'],
      });
    }
    const comspec = process.env.ComSpec || Bun.which('cmd') || 'cmd.exe';
    profiles.push({
      id: 'cmd',
      label: 'Command Prompt',
      shell: comspec,
      args: [],
    });
    return profiles;
  }

  const profiles: WebShellProfile[] = [];
  const envShell = process.env.SHELL?.trim();
  if (envShell && existsSync(envShell)) {
    const base = envShell.split('/').pop() || 'shell';
    profiles.push({
      id: base,
      label: base,
      shell: envShell,
      args: ['-i'],
    });
  }
  for (const candidate of ['/bin/bash', '/bin/zsh', '/bin/sh']) {
    if (existsSync(candidate) && !profiles.some((p) => p.shell === candidate)) {
      const base = candidate.split('/').pop()!;
      profiles.push({
        id: base,
        label: base,
        shell: candidate,
        args: candidate.endsWith('/sh') ? [] : ['-i'],
      });
    }
  }
  return profiles;
}

export function resolveShellProfile(shellId?: string | null): WebShellProfile {
  const profiles = detectHostShellProfiles();
  if (shellId) {
    const found = profiles.find((p) => p.id === shellId);
    if (found) return found;
  }
  return profiles[0] ?? {
    id: 'sh',
    label: 'Shell',
    shell: process.platform === 'win32' ? 'cmd.exe' : '/bin/sh',
    args: [],
  };
}

export function resolveSafeCwd(requested?: string | null): string {
  const candidate = requested?.trim();
  if (candidate && existsSync(candidate)) {
    try {
      if (statSync(candidate).isDirectory()) return candidate;
    } catch {
      // fall through to homedir
    }
  }
  return homedir();
}

function sendJson(ws: WsTerminalClient, payload: Record<string, unknown>): void {
  try {
    ws.send(JSON.stringify(payload));
  } catch {
    // socket already closed
  }
}

export function createInteractiveShellSession(options: {
  ws: WsTerminalClient;
  shellId?: string | null;
  cwd?: string | null;
}): WsTerminalSession {
  const profile = resolveShellProfile(options.shellId);
  const cwd = resolveSafeCwd(options.cwd);
  const decoder = new TextDecoder();
  const isWindows = process.platform === 'win32';
  let lineBuffer = '';
  let state: ActivePipeShell | null = null;

  try {
    const proc = Bun.spawn([profile.shell, ...profile.args], {
      cwd,
      stdin: 'pipe',
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ...process.env,
        TERM: process.env.TERM || 'xterm-256color',
      },
    });
    state = { proc, closed: false };

    sendJson(options.ws, {
      type: 'ready',
      mode: 'interactive',
      shell: profile.id,
      title: profile.label,
      cwd,
    });

    const pumpStream = async (stream: ReadableStream<Uint8Array> | number | undefined) => {
      if (!stream || typeof stream === 'number') return;
      const reader = stream.getReader();
      try {
        while (!state?.closed) {
          const { done, value } = await reader.read();
          if (done) break;
          if (value && value.length > 0) {
            const text = decoder.decode(value, { stream: true });
            const normalized = isWindows ? text.replace(/\r?\n/g, '\r\n') : text;
            sendJson(options.ws, { type: 'data', data: normalized });
          }
        }
      } catch {
        // stream closed
      } finally {
        try {
          reader.releaseLock();
        } catch {
          // ignore
        }
      }
    };

    void pumpStream(proc.stdout);
    void pumpStream(proc.stderr);
    void proc.exited.then((code) => {
      if (state && !state.closed) {
        state.closed = true;
        sendJson(options.ws, { type: 'exit', code });
      }
    });
  } catch (error) {
    sendJson(options.ws, {
      type: 'error',
      message: error instanceof Error ? error.message : 'Failed to start shell process.',
    });
    sendJson(options.ws, { type: 'exit', code: 1 });
  }

  function writeToStdin(data: string): void {
    if (!state || state.closed) return;
    const stdin = state.proc.stdin;
    if (!stdin || typeof stdin === 'number') return;

    // Pipe-backed shells on Windows/POSIX do not have a kernel line discipline, so
    // echo printable characters locally and flush complete lines on Enter.
    for (const ch of data) {
      if (ch === '\x03') {
        lineBuffer = '';
        sendJson(options.ws, { type: 'data', data: '^C\r\n' });
        continue;
      }
      if (ch === '\r' || ch === '\n') {
        sendJson(options.ws, { type: 'data', data: '\r\n' });
        stdin.write(lineBuffer + '\n');
        stdin.flush();
        lineBuffer = '';
        continue;
      }
      if (ch === '\x7f' || ch === '\b') {
        if (lineBuffer.length > 0) {
          lineBuffer = lineBuffer.slice(0, -1);
          sendJson(options.ws, { type: 'data', data: '\b \b' });
        }
        continue;
      }
      lineBuffer += ch;
      sendJson(options.ws, { type: 'data', data: ch });
    }
  }

  return {
    onMessage(raw) {
      const text = typeof raw === 'string' ? raw : Buffer.from(raw).toString('utf8');
      let msg: { type?: string; data?: string; cols?: number; rows?: number };
      try {
        msg = JSON.parse(text) as { type?: string; data?: string; cols?: number; rows?: number };
      } catch {
        writeToStdin(text);
        return;
      }
      if (msg.type === 'input' && typeof msg.data === 'string') {
        writeToStdin(msg.data);
      } else if (msg.type === 'kill') {
        this.close();
      }
    },
    close() {
      if (!state || state.closed) return;
      state.closed = true;
      try {
        state.proc.kill();
      } catch {
        // already exited
      }
    },
  };
}

/**
 * Bridge an authenticated WebSocket client to a Core-owned agent terminal session
 * via MapTerminalEndpoints() (/api/v1/terminals/:id/stdin & /kill) + Core SSE.
 */
export function createAgentTerminalBridgeSession(options: {
  ws: WsTerminalClient;
  terminalSessionId: string;
  runId?: string | null;
  forwardHeaders: Record<string, string>;
  fetchImpl?: typeof fetch;
}): WsTerminalSession {
  const fetchImpl = options.fetchImpl ?? fetch;
  const abort = new AbortController();
  let closed = false;

  sendJson(options.ws, {
    type: 'ready',
    mode: 'agent',
    terminal_session_id: options.terminalSessionId,
    run_id: options.runId ?? null,
  });

  // Follow Core event stream (either run stream or session events) for terminal.* frames.
  void (async () => {
    const streamPath = options.runId
      ? `/api/v1/runs/${encodeURIComponent(options.runId)}/stream`
      : '/api/v1/events';
    try {
      const response = await fetchImpl(coreEndpoint(streamPath), {
        headers: {
          ...options.forwardHeaders,
          accept: 'text/event-stream',
        },
        signal: abort.signal,
      });
      if (!response.ok || !response.body) {
        sendJson(options.ws, { type: 'error', message: `Core stream returned ${response.status}` });
        return;
      }
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      while (!closed && !abort.signal.aborted) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer = (buffer + decoder.decode(value, { stream: true })).replace(/\r\n/g, '\n');
        let idx: number;
        while ((idx = buffer.indexOf('\n\n')) >= 0) {
          const block = buffer.slice(0, idx);
          buffer = buffer.slice(idx + 2);
          handleSseBlock(block);
        }
      }
    } catch {
      if (!closed && !abort.signal.aborted) {
        sendJson(options.ws, { type: 'error', message: 'Terminal stream disconnected.' });
      }
    }
  })();

  function handleSseBlock(block: string): void {
    let eventType = 'message';
    const dataLines: string[] = [];
    for (const line of block.split('\n')) {
      if (!line || line.startsWith(':')) continue;
      const sep = line.indexOf(':');
      const key = sep < 0 ? line : line.slice(0, sep);
      const val = sep < 0 ? '' : line.slice(sep + 1).replace(/^ /, '');
      if (key === 'event') eventType = val;
      else if (key === 'data') dataLines.push(val);
    }
    if (dataLines.length === 0) return;
    try {
      const parsed = JSON.parse(dataLines.join('\n')) as Record<string, unknown>;
      const payload = (parsed.payload && typeof parsed.payload === 'object'
        ? parsed.payload
        : parsed) as Record<string, unknown>;
      const kind = String(parsed.kind ?? parsed.event_type ?? eventType);
      if (payload.terminal_session_id !== options.terminalSessionId) return;
      if (kind === 'terminal.stdout' && typeof payload.data === 'string') {
        sendJson(options.ws, { type: 'data', data: payload.data });
      } else if (kind === 'terminal.exit') {
        sendJson(options.ws, { type: 'exit', code: payload.exit_code ?? null });
      } else if (kind === 'terminal.session.killed') {
        sendJson(options.ws, { type: 'exit', code: -1, killed: true });
      }
    } catch {
      // ignore non-JSON SSE block
    }
  }

  return {
    onMessage(raw) {
      const text = typeof raw === 'string' ? raw : Buffer.from(raw).toString('utf8');
      let msg: { type?: string; data?: string };
      try {
        msg = JSON.parse(text) as { type?: string; data?: string };
      } catch {
        return;
      }
      if (msg.type === 'input' && typeof msg.data === 'string' && msg.data.length > 0) {
        void fetchImpl(
          coreEndpoint(`/api/v1/terminals/${encodeURIComponent(options.terminalSessionId)}/stdin`),
          {
            method: 'POST',
            headers: {
              ...options.forwardHeaders,
              'content-type': 'application/json',
            },
            body: JSON.stringify({ data: msg.data }),
          },
        ).catch(() => {
          sendJson(options.ws, { type: 'error', message: 'Failed to forward stdin to Core.' });
        });
      } else if (msg.type === 'kill') {
        void fetchImpl(
          coreEndpoint(`/api/v1/terminals/${encodeURIComponent(options.terminalSessionId)}/kill`),
          {
            method: 'POST',
            headers: options.forwardHeaders,
          },
        ).catch(() => undefined);
      }
    },
    close() {
      if (closed) return;
      closed = true;
      abort.abort();
    },
  };
}
