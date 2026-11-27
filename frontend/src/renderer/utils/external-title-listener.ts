import { useTerminalStore } from '../state/terminal-store';

// Main is the source of truth for file titles. Remember the latest title per
// PTY and re-apply it whenever the store disagrees: this covers panes that
// enter the store after their first title event and store actions that
// write back an older pane snapshot after awaiting main.
export function subscribeToExternalTitles(): () => void {
  const latest = new Map<string, string>();
  const sync = () => {
    const state = useTerminalStore.getState();
    for (const [id, title] of latest) {
      const pane = state.terminals.get(id);
      if (pane && !pane.externalTitleDisabled && (pane.externalTitle ?? '') !== title) {
        state.setExternalTitle(id, title);
      }
    }
  };
  const unsubscribeTitle = window.terminalAPI.onPtyExternalTitle((id, title) => {
    latest.set(id, title);
    sync();
  });
  const unsubscribeStore = useTerminalStore.subscribe(sync);
  const unsubscribeExit = window.terminalAPI.onPtyExit((id) => latest.delete(id));
  return () => {
    unsubscribeTitle();
    unsubscribeStore();
    unsubscribeExit();
  };
}
