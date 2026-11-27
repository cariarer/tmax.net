// End-to-end smoke test of the backend over the real WebSocket protocol.
// Starts `tmax --headless`, connects like the frontend would, spawns a shell,
// runs a command and checks the output. Also checks that a wrong token and a
// shell outside the profiles are refused, and that config/session persist.
// Uses a throwaway data dir. Usage: npm run test:rpc  (after dotnet build)
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const binDir = join(root, 'src', 'Tmax.App', 'bin', 'Debug', 'net10.0');
const exe = join(binDir, process.platform === 'win32' ? 'tmax.exe' : 'tmax');
if (!existsSync(exe)) throw new Error(`Build first: ${exe} not found`);

const dataDir = mkdtempSync(join(tmpdir(), 'tmax-smoke-'));
const host = spawn(exe, ['--headless'], { stdio: ['ignore', 'pipe', 'inherit'], env: { ...process.env, TMAX_DATA_DIR: dataDir } });
const cleanup = () => { host.kill(); rmSync(dataDir, { recursive: true, force: true }); };
const fail = (msg) => {
  console.error(`FAIL: ${msg}`);
  cleanup();
  process.exit(1);
};
setTimeout(() => fail('timeout'), 20000).unref();

const uiUrl = await new Promise((resolve) => {
  host.once('exit', (code) => fail(`backend exited early (code ${code}); is DOTNET_ROOT set for .NET 10?`));
  let buf = '';
  host.stdout.on('data', (chunk) => {
    buf += chunk;
    const m = buf.match(/TMAX_UI_URL=(\S+)/);
    if (m) resolve(m[1]);
  });
});
const params = new URLSearchParams(new URL(uiUrl).hash.slice(1));
const port = params.get('port');
const token = params.get('token');

const rejected = (url) =>
  new Promise((resolve) => {
    const ws = new WebSocket(url);
    ws.onopen = () => { ws.close(); resolve(false); };
    ws.onerror = () => resolve(true);
  });

if (!(await rejected(`ws://127.0.0.1:${port}/ws?token=wrong`))) fail('wrong token was accepted');
console.log('ok  wrong token refused');

const ws = new WebSocket(`ws://127.0.0.1:${port}/ws?token=${token}`);
await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
console.log('ok  connected with token');

let nextId = 1;
const pending = new Map();
let output = '';
const invoke = (channel, ...args) => new Promise((resolve, reject) => {
  const id = nextId++;
  pending.set(id, { resolve, reject });
  ws.send(JSON.stringify({ type: 'invoke', id, channel, args }));
});
ws.onmessage = (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.type === 'reply') {
    const p = pending.get(msg.id);
    pending.delete(msg.id);
    msg.error != null ? p.reject(new Error(msg.error)) : p.resolve(msg.result);
  } else if (msg.channel === 'pty:data') {
    output += msg.args[1];
  }
};

const info = await invoke('platform:getInfo');
console.log(`ok  platform:getInfo -> ${info.platform}`);

const config = await invoke('config:get');
if (!config.shells?.length || !config.defaultShellId || !config.theme) fail('config:get misses defaults');
await invoke('config:set', 'tabMode', 'workspaces');
if ((await invoke('config:get')).tabMode !== 'workspaces') fail('config:set not applied');
if (JSON.parse(readFileSync(join(dataDir, 'tmax-config.json'), 'utf8')).tabMode !== 'workspaces') fail('config not persisted');
console.log(`ok  config get/set persisted (${config.shells.length} shells, default ${config.defaultShellId})`);

await invoke('session:save', { tabs: ['a'] });
if ((await invoke('session:load'))?.tabs?.[0] !== 'a') fail('session round trip');
console.log('ok  session save/load');

for (const provider of ['copilot', 'claude-code']) {
  const list = await invoke(`${provider}:listSessions`, 5);
  if (!Array.isArray(list?.sessions) || typeof list.totalEligible !== 'number') fail(`${provider}:listSessions has the wrong shape`);
}
console.log('ok  AI session lists have the renderer shape');

try {
  await invoke('pty:create', { id: 'x', shellPath: '/usr/bin/env', cols: 80, rows: 24 });
  fail('shell outside profiles was accepted');
} catch {
  console.log('ok  foreign shell refused');
}

const created = await invoke('pty:create', { id: 'smoke', shellPath: '', cols: 80, rows: 24 });
console.log(`ok  pty:create -> pid ${created.pid}`);
const cmd = process.platform === 'win32' ? 'echo tmax-%TMAX_PANE_ID%-ok\r' : 'echo tmax-$TMAX_PANE_ID-ok\r';
ws.send(JSON.stringify({ type: 'send', channel: 'pty:write', args: ['smoke', cmd] }));

const deadline = Date.now() + 10000;
while (!output.includes('tmax-smoke-ok') && Date.now() < deadline) await new Promise((r) => setTimeout(r, 50));
if (!output.includes('tmax-smoke-ok')) fail(`marker not seen; output:\n${output}`);
console.log('ok  command output received');

await invoke('pty:kill', 'smoke');

// Graceful shutdown must not hang on open connections (it did until the sockets were tied to app shutdown).
if (process.platform !== 'win32') {
  host.removeAllListeners('exit');
  const exited = new Promise((resolve) => host.once('exit', resolve));
  const closed = new Promise((resolve) => { ws.onclose = resolve; });
  host.kill('SIGTERM');
  const ok = await Promise.race([Promise.all([exited, closed]).then(() => true), new Promise((r) => setTimeout(() => r(false), 6000))]);
  if (!ok) fail('backend did not shut down on SIGTERM with a client connected');
  console.log('ok  SIGTERM shuts down and closes client connections');
} else {
  ws.close();
}
cleanup();
console.log('PASS');
process.exit(0);
