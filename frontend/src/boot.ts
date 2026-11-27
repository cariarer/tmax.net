// Connects to the .NET backend, installs window.terminalAPI / window.platformInfo
// (what the Electron preload used to expose) and then starts the original renderer.
import { Backend } from './backend';
import { createTerminalAPI, type PlatformInfo } from './bridge/terminal-api';
import { installClipboardCapture } from './bridge/clipboard';
import { showTerminalContextMenu } from './bridge/context-menu';

/** Which native window this renderer runs in (set by WindowManager in the URL fragment). */
function windowKey(): string {
  return new URLSearchParams(window.location.hash.slice(1)).get('win') ?? 'main';
}

/** The renderer sets document.title (focused pane / external title); mirror it to the OS window. */
function mirrorTitle(backend: Backend, key: string) {
  let last = '';
  const push = () => {
    if (document.title === last) return;
    last = document.title;
    backend.send('window:setTitle', key, last);
  };
  const head = document.querySelector('title') ?? document.head.appendChild(document.createElement('title'));
  new MutationObserver(push).observe(head, { childList: true, characterData: true, subtree: true });
  push();
}

async function boot() {
  const key = windowKey();
  const backend = await Backend.connect();
  const platformInfo = await backend.invoke<PlatformInfo>('platform:getInfo');

  installClipboardCapture();
  Object.defineProperty(window, 'platformInfo', { value: Object.freeze(platformInfo) });
  Object.defineProperty(window, 'terminalAPI', { value: Object.freeze(createTerminalAPI(backend, showTerminalContextMenu)) });
  backend.on('backend:disconnected', () => {
    document.title = 'tmax - backend disconnected';
  });

  mirrorTitle(backend, key);
  await import('./renderer/index');
}

boot().catch((err) => {
  console.error('[tmax.net] boot failed', err);
  document.body.innerHTML = `<pre style="color:#f38ba8;padding:16px;font:13px monospace">tmax.net could not start:\n${String(err?.message ?? err)}</pre>`;
});
