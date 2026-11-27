/**
 * Clipboard access for the WebView, replacing Electron's synchronous `clipboard` module.
 *
 * A WebView has no synchronous clipboard read. Two sources are used instead:
 *  - inside a `paste` event the browser hands over the clipboard synchronously
 *    (`event.clipboardData`); a capture listener records it so the original
 *    sync API (`clipboardRead()`, `clipboardHasImage()` …) works in paste handlers;
 *  - everywhere else (Ctrl+V keydown, context menu) callers await `readSnapshot()`,
 *    which uses the async Clipboard API.
 */

export interface ClipboardSnapshot {
  plainText: string;
  html: string;
  image: Blob | null;
}

const EMPTY: ClipboardSnapshot = { plainText: '', html: '', image: null };

let current: ClipboardSnapshot = EMPTY;
let currentEventId = 0;
let snapshotEventId = -1;

function fromDataTransfer(dt: DataTransfer | null): ClipboardSnapshot {
  if (!dt) return EMPTY;
  let image: Blob | null = null;
  for (const item of Array.from(dt.items ?? [])) {
    if (item.kind === 'file' && item.type.startsWith('image/')) {
      image = item.getAsFile();
      if (image) break;
    }
  }
  return { plainText: dt.getData('text/plain') ?? '', html: dt.getData('text/html') ?? '', image };
}

/** Installs the capture listener that records the clipboard of every paste event. */
export function installClipboardCapture(): void {
  window.addEventListener(
    'paste',
    (e) => {
      current = fromDataTransfer(e.clipboardData);
      snapshotEventId = ++currentEventId;
      // Outside the event the data is stale; drop it after the handlers ran.
      queueMicrotask(() => {
        setTimeout(() => {
          if (snapshotEventId === currentEventId) current = EMPTY;
        }, 0);
      });
    },
    true,
  );
}

/** Synchronous view, only meaningful inside a paste event (see module comment). */
export function syncSnapshot(): ClipboardSnapshot {
  return current;
}

/** Reads the clipboard asynchronously (keyboard shortcuts, context menus). */
export async function readSnapshot(): Promise<ClipboardSnapshot> {
  const snap: ClipboardSnapshot = { plainText: '', html: '', image: null };
  try {
    if (navigator.clipboard?.read) {
      const items = await navigator.clipboard.read();
      for (const item of items) {
        for (const type of item.types) {
          if (type === 'text/plain' && !snap.plainText) snap.plainText = await (await item.getType(type)).text();
          else if (type === 'text/html' && !snap.html) snap.html = await (await item.getType(type)).text();
          else if (type.startsWith('image/') && !snap.image) snap.image = await item.getType(type);
        }
      }
      current = snap;
      return snap;
    }
  } catch {
    // read() can be denied or unsupported for some types; fall back to plain text.
  }
  try {
    snap.plainText = (await navigator.clipboard?.readText?.()) ?? '';
  } catch {
    /* no clipboard access */
  }
  current = snap;
  return snap;
}

export function writeText(text: string): void {
  if (navigator.clipboard?.writeText) {
    navigator.clipboard.writeText(text).catch(() => legacyCopy(text));
  } else {
    legacyCopy(text);
  }
}

function legacyCopy(text: string): void {
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.style.position = 'fixed';
  ta.style.opacity = '0';
  document.body.appendChild(ta);
  ta.select();
  try {
    document.execCommand('copy');
  } finally {
    ta.remove();
  }
}

export async function blobToBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}
