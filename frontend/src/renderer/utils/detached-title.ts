export function observeDetachedTitle(
  terminalId: string,
  update: (title: string, isExternal: boolean) => void,
): { setBaseTitle: (title: string) => void; dispose: () => void } {
  let baseTitle = 'Detached terminal';
  let externalTitle = '';
  let titleEvents = 0;
  let active = true;
  const render = () => update(externalTitle || baseTitle, !!externalTitle);
  const unsubscribe = window.terminalAPI.onPtyExternalTitle((id, title) => {
    if (id !== terminalId) return;
    titleEvents++;
    externalTitle = title;
    render();
  });
  window.terminalAPI.getPtyExternalTitle(terminalId).then((title) => {
    if (!active || titleEvents !== 0) return;
    externalTitle = title;
    render();
  }).catch((err) => console.error('Failed to read external pane title:', err));
  return {
    setBaseTitle(title) {
      if (title) baseTitle = title;
      render();
    },
    dispose() {
      active = false;
      unsubscribe();
    },
  };
}
