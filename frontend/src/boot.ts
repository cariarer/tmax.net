// Connects to the .NET backend, installs window.terminalAPI / window.platformInfo
// (what the Electron preload used to expose) and then starts the original renderer.
import { Backend } from './backend';
import { createTerminalAPI, type PlatformInfo } from './bridge/terminal-api';
import { installClipboardCapture } from './bridge/clipboard';
import { showTerminalContextMenu } from './bridge/context-menu';

async function boot() {
  const backend = await Backend.connect();
  const platformInfo = await backend.invoke<PlatformInfo>('platform:getInfo');

  installClipboardCapture();
  Object.defineProperty(window, 'platformInfo', { value: Object.freeze(platformInfo) });
  Object.defineProperty(window, 'terminalAPI', { value: Object.freeze(createTerminalAPI(backend, showTerminalContextMenu)) });
  backend.on('backend:disconnected', () => {
    document.title = 'tmax - backend disconnected';
  });

  await import('./renderer/index');
}

boot().catch((err) => {
  console.error('[tmax.net] boot failed', err);
  document.body.innerHTML = `<pre style="color:#f38ba8;padding:16px;font:13px monospace">tmax.net could not start:\n${String(err?.message ?? err)}</pre>`;
});
