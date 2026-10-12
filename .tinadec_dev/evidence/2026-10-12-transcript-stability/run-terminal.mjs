// Run from the repository root: node .tinadec_dev/evidence/2026-10-12-transcript-stability/run-terminal.mjs
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { parse, compileScript, compileStyle, registerTS } from '@vue/compiler-sfc';
const require = createRequire(import.meta.url);
const { build } = require(path.join(process.cwd(), 'node_modules/vite/node_modules/esbuild'));
const evidence = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(evidence, '../../..');
const desktop = path.join(root, 'apps/desktop/src');
registerTS(() => require('typescript'));
const bundle = await build({
  entryPoints: [path.join(evidence, 'terminal-fixture.ts')], bundle: true, write: false,
  format: 'iife', platform: 'browser', outdir: 'ram', logLevel: 'warning',
  loader: { '.woff': 'dataurl', '.woff2': 'dataurl', '.ttf': 'dataurl' },
  define: { __VUE_OPTIONS_API__: 'true', __VUE_PROD_DEVTOOLS__: 'false', __VUE_PROD_HYDRATION_MISMATCH_DETAILS__: 'false' },
  plugins: [{ name: 'production-sfc-fixture', setup(builder) {
    builder.onResolve({ filter: /^(vue$|mermaid$|@\/|@tinadec\/ui$|\.\.?\/)/ }, ({ path: name, importer }) => {
      if (name === 'vue') return { path: path.join(desktop, 'lib/vue-shim.ts') };
      if (name === 'mermaid') return { path: name, namespace: 'unused' };
      if (['./ComposerBar.vue', './ChatHeader.vue', './WelcomeScreen.vue'].includes(name) && importer.endsWith('ChatPanel.vue')) return { path: name, namespace: 'peripheral' };
      if (name === '@/components/git/DiffViewer.vue') return { path: name, namespace: 'peripheral' };
      if (name === '@/components/ui' || (name === './ui' && importer.endsWith('MessageList.vue'))) return { path: 'ui', namespace: 'narrow-ui' };
      if ((name === '../api' && importer.endsWith('MessageItem.vue'))) return { path: '@/api', namespace: 'boundary' };
      if (['@/api', '@/controllers/HomeController', '@/composables/useNotifications', '@tinadec/ui'].includes(name)) return { path: name, namespace: 'boundary' };
      if (!name.startsWith('@/')) return undefined;
      const target = path.join(desktop, name.slice(2));
      const found = [target, `${target}.ts`, `${target}.vue`, path.join(target, 'index.ts')].find(file => fs.existsSync(file) && fs.statSync(file).isFile());
      return found ? { path: found } : undefined;
    });
    builder.onLoad({ filter: /.*/, namespace: 'boundary' }, ({ path: name }) => ({ loader: 'js', contents:
      name === '@/api' ? 'export const api = Object.fromEntries(["listTerminalSessions", "controlRun", "sendTerminalStdin", "killTerminalSession"].map(name => [name, (...args) => window.__terminalBoundary[name](...args)]));' :
      name === '@/controllers/HomeController' ? 'export const homeController = { onEvent: handler => window.__terminalBoundary.onEvent(handler), events: { value: [] }, currentSession: { value: { id: "fixture-chat" } }, agentTurnActivities: { value: {} } };' :
      name === '@/composables/useNotifications' ? 'export const useNotifications = () => ({ notify: { info: () => {}, error: error => { throw error } } });' :
      'export const useUie = () => { throw new Error("UIE boundary is unused in lifecycle fixture") };' }));
    builder.onLoad({ filter: /.*/, namespace: 'unused' }, () => ({ loader: 'js', contents: 'export default { initialize() { throw new Error("Mermaid is unused in simple-send fixture") } }' }));
    builder.onLoad({ filter: /.*/, namespace: 'peripheral' }, ({ path: name }) => ({ loader: 'js', contents: `import { h } from 'vue'; export default { render() { return h('div', { class: ${JSON.stringify(name.includes('ComposerBar') ? 'composer-box' : 'fixture-peripheral')} }, 'Fixture peripheral control') } };` }));
    builder.onLoad({ filter: /.*/, namespace: 'narrow-ui' }, () => ({ loader: 'js', resolveDir: path.join(desktop, 'components/ui'), contents: `export { default as UiButton } from './button.vue'; export { default as UiCollapsible } from './collapsible.vue'; export { default as UiScrollArea } from './scroll-area.vue';` }));
    builder.onLoad({ filter: /\.vue$/ }, ({ path: filename }) => {
      const { descriptor } = parse(fs.readFileSync(filename, 'utf8'), { filename });
      const id = `fixture-${path.basename(filename).replace(/\W/g, '')}`;
      const script = compileScript(descriptor, { id, inlineTemplate: true, genDefaultAs: '__fixtureComponent', fs: { fileExists: fs.existsSync, readFile: file => fs.readFileSync(file, 'utf8') } });
      const styles = descriptor.styles.map(style => compileStyle({ source: style.content, filename, id, scoped: style.scoped }).code).join('\n');
      return { loader: 'ts', resolveDir: path.dirname(filename), contents: `${script.content}\n__fixtureComponent.__scopeId = ${JSON.stringify(`data-v-${id}`)}; export default __fixtureComponent;\nconst style = document.createElement('style'); style.textContent = ${JSON.stringify(styles)}; document.head.append(style);` };
    });
  } }],
});
const js = bundle.outputFiles.find(file => file.path.endsWith('.js')).text;
const css = bundle.outputFiles.find(file => file.path.endsWith('.css'))?.text ?? '';
const server = http.createServer((request, response) => {
  if (request.url === '/fixture.js') { response.setHeader('Content-Type', 'text/javascript'); response.end(js); return; }
  response.setHeader('Content-Type', 'text/html');
  response.end(`<!doctype html><html><head><meta charset="utf-8"><title>Isolated terminal acceptance</title><style>:root{--bg-primary:#fff;--bg-secondary:#f7f7f7;--text-primary:#222;--text-secondary:#555;--text-muted:#666}body{background:#fff;color:#222;font-family:system-ui}${css}</style></head><body><script src="/fixture.js"></script></body></html>`);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const env = { ...process.env, TERMINAL_FIXTURE_URL: `http://127.0.0.1:${server.address().port}`, TERMINAL_FIXTURE_EVIDENCE: evidence };
delete env.ELECTRON_RUN_AS_NODE; delete env.ELECTRON_NO_ATTACH_CONSOLE;
const child = spawn(require('electron'), [path.join(evidence, 'terminal-electron.cjs')], { env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
child.stdout.pipe(process.stdout); child.stderr.pipe(process.stderr);
const timeout = setTimeout(() => child.kill(), 65000);
const code = await new Promise(resolve => child.on('exit', resolve));
clearTimeout(timeout); server.close(); process.exitCode = code ?? 1;
