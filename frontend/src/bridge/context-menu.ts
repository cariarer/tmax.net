/**
 * HTML replacement for the native Cut/Copy/Paste menu the Electron main process
 * showed for TERMINAL_CONTEXT_MENU. Resolves with 'cut' | 'copy' | 'paste' or null.
 */
const isMac = navigator.platform.toUpperCase().includes('MAC');
const mod = isMac ? '⌘' : 'Ctrl+';

let lastPointer = { x: 0, y: 0 };
window.addEventListener('contextmenu', (e) => { lastPointer = { x: e.clientX, y: e.clientY }; }, true);

export function showTerminalContextMenu(): Promise<string | null> {
  return new Promise((resolve) => {
    const menu = document.createElement('div');
    menu.className = 'tmaxnet-context-menu';
    menu.setAttribute('role', 'menu');
    Object.assign(menu.style, {
      position: 'fixed', zIndex: '100000', minWidth: '160px', padding: '4px',
      background: 'var(--bg-secondary, #2a2a3a)', color: 'var(--text-primary, #cdd6f4)',
      border: '1px solid var(--border, #45475a)', borderRadius: '6px',
      boxShadow: '0 6px 24px rgba(0,0,0,.35)', font: '13px system-ui, sans-serif',
    } satisfies Partial<CSSStyleDeclaration>);

    let done = false;
    const finish = (value: string | null) => {
      if (done) return;
      done = true;
      menu.remove();
      window.removeEventListener('mousedown', onOutside, true);
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('blur', onBlur);
      resolve(value);
    };
    const onOutside = (e: MouseEvent) => { if (!menu.contains(e.target as Node)) finish(null); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); finish(null); } };
    const onBlur = () => finish(null);

    for (const [action, label, key] of [['cut', 'Cut', 'X'], ['copy', 'Copy', 'C'], ['paste', 'Paste', 'V']]) {
      const item = document.createElement('div');
      item.setAttribute('role', 'menuitem');
      Object.assign(item.style, { display: 'flex', justifyContent: 'space-between', gap: '24px', padding: '5px 10px', borderRadius: '4px', cursor: 'default' });
      item.innerHTML = `<span>${label}</span><span style="opacity:.6">${mod}${key}</span>`;
      item.onmouseenter = () => { item.style.background = 'var(--accent, #89b4fa)'; item.style.color = '#11111b'; };
      item.onmouseleave = () => { item.style.background = ''; item.style.color = ''; };
      item.onmousedown = (e) => e.preventDefault(); // keep terminal focus
      item.onclick = () => finish(action);
      menu.appendChild(item);
    }

    document.body.appendChild(menu);
    const { innerWidth: w, innerHeight: h } = window;
    const r = menu.getBoundingClientRect();
    menu.style.left = `${Math.min(lastPointer.x, w - r.width - 4)}px`;
    menu.style.top = `${Math.min(lastPointer.y, h - r.height - 4)}px`;

    // Defer so the right-click that opened the menu doesn't close it.
    setTimeout(() => {
      window.addEventListener('mousedown', onOutside, true);
      window.addEventListener('keydown', onKey, true);
      window.addEventListener('blur', onBlur);
    }, 0);
  });
}
