// Proof of concept: one xterm.js terminal wired to a backend PTY.
// Will be replaced by the ported React renderer once the terminalAPI shim exists.
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { Backend } from './backend';

interface PlatformInfo {
  platform: 'win32' | 'darwin' | 'linux';
  homeDir: string;
  windowsBuildNumber: number;
}

const status = document.getElementById('status')!;

async function start(): Promise<void> {
  const backend = await Backend.connect();
  const platform = await backend.invoke<PlatformInfo>('platform:getInfo');
  status.textContent = `${platform.platform} · connected`;

  const term = new Terminal({
    fontFamily: 'Menlo, Consolas, "DejaVu Sans Mono", monospace',
    fontSize: 13,
    cursorBlink: true,
    allowProposedApi: true,
    theme: { background: '#1e1e1e' },
    ...(platform.platform === 'win32'
      ? { windowsPty: { backend: 'conpty' as const, buildNumber: platform.windowsBuildNumber } }
      : {}),
  });
  const fit = new FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('terminal')!);
  fit.fit();

  const id = `pane-${crypto.randomUUID()}`;
  backend.on('pty:data', (paneId: string, data: string) => {
    if (paneId === id) term.write(data);
  });
  backend.on('pty:exit', (paneId: string, code: number | null) => {
    if (paneId !== id) return;
    term.write(`\r\n\x1b[90m[process exited${code != null ? ` with code ${code}` : ''}]\x1b[0m\r\n`);
    status.textContent = 'exited';
  });
  backend.on('backend:disconnected', () => (status.textContent = 'disconnected'));

  await backend.invoke('pty:create', {
    id,
    shellPath: '', // platform default
    args: null,
    cwd: platform.homeDir,
    cols: term.cols,
    rows: term.rows,
  });

  term.onData((data) => backend.send('pty:write', id, data));
  term.onResize(({ cols, rows }) => void backend.invoke('pty:resize', id, cols, rows));
  new ResizeObserver(() => fit.fit()).observe(document.getElementById('terminal')!);
  term.focus();
}

start().catch((err) => {
  console.error(err);
  status.textContent = `error: ${err.message}`;
});
