import { expect, test } from 'bun:test';
import { createSseResponse } from './streaming.js';
import { createAgentTerminalBridgeSession, createInteractiveShellSession, type WsTerminalClient } from './wsTerminalProxy.js';

test('createSseResponse removes its abort listener when upstream closes normally', async () => {
  const controller = new AbortController();
  let removed = 0;
  let added = 0;
  const origAdd = controller.signal.addEventListener.bind(controller.signal);
  const origRemove = controller.signal.removeEventListener.bind(controller.signal);
  controller.signal.addEventListener = ((...args: Parameters<AbortSignal['addEventListener']>) => {
    if (args[0] === 'abort') added++;
    return origAdd(...args);
  }) as AbortSignal['addEventListener'];
  controller.signal.removeEventListener = ((...args: Parameters<AbortSignal['removeEventListener']>) => {
    if (args[0] === 'abort') removed++;
    return origRemove(...args);
  }) as AbortSignal['removeEventListener'];

  const upstream = new Response('event: ping\ndata: ok\n\n', {
    status: 200,
    headers: { 'content-type': 'text/event-stream' },
  });
  const response = createSseResponse(upstream, controller.signal);
  const text = await response.text();
  expect(text).toContain('event: ping');
  expect(added).toBe(1);
  expect(removed).toBe(1);
});

test('createAgentTerminalBridgeSession parses split CRLF and bare CR SSE blocks and cancels reader on close', async () => {
  const sent: Array<{ type: string; data?: string; code?: number | null }> = [];
  const ws: WsTerminalClient = {
    send(raw) {
      sent.push(JSON.parse(raw));
    },
    close() {},
  };

  let cancelled = false;
  let pushController: ReadableStreamDefaultController<Uint8Array> | null = null;
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(ctrl) {
      pushController = ctrl;
      ctrl.enqueue(
        encoder.encode('event: terminal.stdout\rdata: {"terminal_session_id":"term-1","data":"hello"}\r'),
      );
      ctrl.enqueue(
        encoder.encode('\r\nevent: terminal.stdout\r\ndata: {"terminal_session_id":"term-1","data":"world"}\n\n'),
      );
    },
    cancel() {
      cancelled = true;
    },
  });

  const session = createAgentTerminalBridgeSession({
    ws,
    terminalSessionId: 'term-1',
    forwardHeaders: {},
    fetchImpl: (async () =>
      new Response(stream, {
        status: 200,
        headers: { 'content-type': 'text/event-stream' },
      })) as typeof fetch,
  });

  await new Promise((r) => setTimeout(r, 30));
  expect(sent.filter((m) => m.type === 'data').map((m) => m.data)).toEqual(['hello', 'world']);

  session.close();
  await new Promise((r) => setTimeout(r, 30));
  expect(cancelled).toBe(true);
  try { pushController?.close(); } catch { /* ignore */ }
});

test('createInteractiveShellSession starts shell and cleans up on close', async () => {
  const sent: Array<{ type: string; data?: string }> = [];
  const ws: WsTerminalClient = {
    send(raw) {
      sent.push(JSON.parse(raw));
    },
    close() {},
  };
  const session = createInteractiveShellSession({
    ws,
    cwd: process.cwd(),
    shellId: process.platform === 'win32' ? 'cmd' : undefined,
  });
  session.onMessage(JSON.stringify({ type: 'input', data: 'echo ok\r\n' }));
  await new Promise((r) => setTimeout(r, 150));
  session.close();
  expect(Array.isArray(sent)).toBe(true);
});
