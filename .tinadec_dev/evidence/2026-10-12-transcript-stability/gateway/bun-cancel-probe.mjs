import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath, pathToFileURL } from 'node:url';
const script = fileURLToPath(import.meta.url), repo = path.resolve(path.dirname(script), '../../../..');
const localLogs = path.join(repo, '.tinadec_dev/tmp/gateway-bun-cancel');
if (process.argv[2] === 'child') {
  const mode = process.argv[3];
  const proxy = async request => {
    const response = await fetch(process.env.PROBE_UPSTREAM, { signal: request.signal });
    const headers = new Headers(response.headers);
    if (mode === 'elysia-production') {
      const { createStreamProxyResponse } = await import(pathToFileURL(path.join(repo, 'TinadecGateway/src/streamResponse.ts')).href);
      return createStreamProxyResponse(response);
    }
    if (mode !== 'elysia-chunked') { headers.delete('transfer-encoding'); headers.delete('connection'); }
    return new Response(response.body, { status: response.status, headers });
  };
  let server;
  if (mode === 'native') server = Bun.serve({ hostname: '127.0.0.1', port: 0, idleTimeout: 60, fetch: proxy });
  else {
    const { Elysia } = await import(pathToFileURL(path.join(repo, 'TinadecGateway/node_modules/elysia/dist/index.mjs')).href);
    const app = new Elysia().get('/sse', async ({ request, set }) => {
      set.headers['content-type'] = 'text/event-stream';
      set.headers['cache-control'] = 'no-cache';
      return proxy(request);
    });
    app.listen({ hostname: '127.0.0.1', port: 0, idleTimeout: 60 }); server = app.server;
  }
  console.log(JSON.stringify({ port: server.port }));
} else {
  const sockets = new Set();
  const upstream = http.createServer((_request, response) => {
    response.writeHead(200, { 'content-type': 'text/event-stream', 'transfer-encoding': 'chunked' });
    response.write('event: stream.ready\ndata: {}\n\n');
    const heartbeat = setInterval(() => response.write(': heartbeat\n\n'), 3);
    response.once('close', () => clearInterval(heartbeat));
  });
  upstream.on('connection', socket => { sockets.add(socket); socket.once('close', () => sockets.delete(socket)); });
  upstream.listen(0, '127.0.0.1'); await once(upstream, 'listening');
  const address = upstream.address(), results = [];
  try {
    fs.mkdirSync(localLogs, { recursive: true });
    for (const mode of ['elysia-chunked', 'elysia-stripped', 'elysia-production', 'native']) {
      let output = '', errors = '', completed = 0, port;
      const child = spawn(process.env.TRANSCRIPT_BUN_BINARY ?? 'bun', [script, 'child', mode], { env: { ...process.env, PROBE_UPSTREAM: `http://127.0.0.1:${address.port}/sse` }, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
      const timeout = setTimeout(() => child.kill(), 30000);
      child.stdout.on('data', chunk => { output += chunk; if (!port) { try { port = JSON.parse(output.split('\n')[0]).port; } catch {} } });
      child.stderr.on('data', chunk => errors += chunk);
      try {
        const start = Date.now();
        while (!port && child.exitCode === null && Date.now() - start < 4000) await new Promise(r => setTimeout(r, 10));
        if (!port) throw new Error('Child did not start: ' + errors.slice(0, 500));
        for (let batch = 0; batch < 20 && child.exitCode === null; batch++) {
          await Promise.all(Array.from({ length: 5 }, async (_v, index) => {
            const controller = new AbortController();
            try {
              const response = await fetch(`http://127.0.0.1:${port}/sse`, { signal: controller.signal });
              const reader = response.body.getReader(); await reader.read();
              if (index % 2) await new Promise(r => setTimeout(r, 1));
              controller.abort(); await reader.read().catch(() => {}); completed++;
            } catch (error) { if (!controller.signal.aborted) throw error; }
          }));
        }
        await new Promise(r => setTimeout(r, 500));
        results.push({ mode, completed, exited: child.exitCode !== null, exitCode: child.exitCode, abortErrors: (errors.match(/The connection was closed/g) ?? []).length, panic: errors.includes('panic(') });
      } catch (error) { results.push({ mode, completed, message: error.message }); }
      finally { clearTimeout(timeout); if (child.exitCode === null) child.kill(); await once(child, 'close').catch(() => {}); }
      fs.writeFileSync(path.join(localLogs, `${mode}.log`), output + '\n' + errors);
    }
  } finally { for (const socket of sockets) socket.destroy(); await new Promise(resolve => upstream.close(resolve)); }
  fs.writeFileSync(path.join(path.dirname(script), 'bun-cancel-production-comparison.json'), JSON.stringify({ platform: process.platform, node: process.version, checked_at: new Date().toISOString(), requests_per_mode: 100, isolated_node_http_upstream: true, real_socket_disconnect: true, results }, null, 2));
  console.log(JSON.stringify(results));
}
