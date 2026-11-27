import { beforeAll, beforeEach, describe, expect, test, vi } from 'vitest';
import type { TerminalInstance } from '../../../src/renderer/state/types';
import type { CopilotSessionSummary } from '../../../src/shared/copilot-types';

let titleListener: (id: string, title: string) => void;
let exitListener: (id: string) => void;
const disablePtyExternalTitle = vi.fn();
const saveSession = vi.fn(async (_value: unknown) => {});
const getPtyExternalTitle = vi.fn(async (_id: string) => '');

(globalThis as any).window = {
  terminalAPI: {
    disablePtyExternalTitle,
    getPtyExternalTitle,
    saveSession,
    killPty: vi.fn(async () => {}),
    diagLog: vi.fn(),
    onPtyExternalTitle: (listener: typeof titleListener) => {
      titleListener = listener;
      return () => {};
    },
    onPtyExit: (listener: typeof exitListener) => {
      exitListener = listener;
      return () => {};
    },
  },
};
(globalThis as any).document = { hasFocus: () => true };

let useTerminalStore: typeof import('../../../src/renderer/state/terminal-store').useTerminalStore;
let subscribeToExternalTitles: typeof import('../../../src/renderer/utils/external-title-listener').subscribeToExternalTitles;
let getTerminalDisplayTitle: typeof import('../../../src/renderer/state/types').getTerminalDisplayTitle;
let observeDetachedTitle: typeof import('../../../src/renderer/utils/detached-title').observeDetachedTitle;

const pane = (): TerminalInstance => ({
  id: 'pane-1',
  title: 'Shell',
  customTitle: false,
  shellProfileId: 'shell',
  cwd: '/project',
  mode: 'tiled',
  pid: 123,
  lastProcess: '',
  startupCommand: '',
});

const session: CopilotSessionSummary = {
  id: 'session-1',
  provider: 'copilot',
  status: 'thinking',
  cwd: '/project',
  branch: '',
  repository: '',
  summary: 'Session title',
  messageCount: 1,
  toolCallCount: 0,
  lastActivityTime: Date.now(),
};

beforeAll(async () => {
  ({ useTerminalStore } = await import('../../../src/renderer/state/terminal-store'));
  ({ subscribeToExternalTitles } = await import('../../../src/renderer/utils/external-title-listener'));
  ({ getTerminalDisplayTitle } = await import('../../../src/renderer/state/types'));
  ({ observeDetachedTitle } = await import('../../../src/renderer/utils/detached-title'));
});

beforeEach(() => {
  disablePtyExternalTitle.mockClear();
  getPtyExternalTitle.mockReset().mockResolvedValue('');
  saveSession.mockClear();
  useTerminalStore.setState({
    terminals: new Map([['pane-1', pane()]]),
    focusedTerminalId: 'pane-1',
    layout: { tilingRoot: { kind: 'leaf', terminalId: 'pane-1' }, floatingPanels: [] },
    sessionNameOverrides: {},
    copilotSessions: [],
    claudeCodeSessions: [],
    closedTerminals: [],
    confirmOnCloseSession: false,
  });
});

describe('external pane titles', () => {
  test('keeps the AI session title and persisted name separate from the overlay', () => {
    const store = useTerminalStore.getState();
    store.setExternalTitle('pane-1', 'Temporary');
    store.updateTerminalTitleFromSession(session, 'copilot');

    let current = useTerminalStore.getState().terminals.get('pane-1')!;
    expect(current.title).toBe('Session title');
    expect(getTerminalDisplayTitle(current)).toBe('Temporary');
    expect(useTerminalStore.getState().sessionNameOverrides).toEqual({});

    store.setExternalTitle('pane-1', '');
    current = useTerminalStore.getState().terminals.get('pane-1')!;
    expect(getTerminalDisplayTitle(current)).toBe('Session title');
  });

  test('manual rename wins over later file changes', () => {
    const store = useTerminalStore.getState();
    store.setExternalTitle('pane-1', 'Temporary');
    store.renameTerminal('pane-1', 'My pane', true);
    store.setExternalTitle('pane-1', 'Later');

    expect(getTerminalDisplayTitle(useTerminalStore.getState().terminals.get('pane-1'))).toBe('My pane');
    expect(disablePtyExternalTitle).toHaveBeenCalledWith('pane-1');
  });

  test('renaming the linked AI session also replaces the external title', () => {
    useTerminalStore.setState({
      terminals: new Map([['pane-1', { ...pane(), aiSessionId: 'session-1' }]]),
    });
    const store = useTerminalStore.getState();
    store.setExternalTitle('pane-1', 'Temporary');
    store.setSessionNameOverride('session-1', 'Named session');
    store.setExternalTitle('pane-1', 'Later');

    expect(getTerminalDisplayTitle(useTerminalStore.getState().terminals.get('pane-1'))).toBe('Named session');
    expect(disablePtyExternalTitle).toHaveBeenCalledWith('pane-1');
  });

  test('a title arriving while a pane detaches is kept', async () => {
    let finishDetach!: () => void;
    (window as any).terminalAPI.detachTerminal = vi.fn(() => new Promise<void>((resolve) => { finishDetach = resolve; }));
    const detaching = useTerminalStore.getState().detachTerminal('pane-1');
    useTerminalStore.getState().setExternalTitle('pane-1', 'During detach');
    finishDetach();
    await detaching;

    const current = useTerminalStore.getState().terminals.get('pane-1')!;
    expect(current.mode).toBe('detached');
    expect(getTerminalDisplayTitle(current)).toBe('During detach');
  });

  test('keeps temporary titles out of layouts and undo-close snapshots', async () => {
    const store = useTerminalStore.getState();
    store.setExternalTitle('pane-1', 'Temporary');
    await store.saveNamedLayout('overlay-test');
    const saved = saveSession.mock.calls[0][0] as {
      layouts: Record<string, { tree: { terminal: { title: string } } }>;
    };
    expect(saved.layouts['overlay-test'].tree.terminal.title).toBe('Shell');

    await store.closeTerminal('pane-1', { skipConfirm: true });
    expect(useTerminalStore.getState().closedTerminals[0]).toMatchObject({
      kind: 'pane', title: 'Shell', customTitle: false,
    });
  });

  test('applies the latest title received before the pane enters the store', () => {
    useTerminalStore.setState({ terminals: new Map() });
    const unsubscribe = subscribeToExternalTitles();
    titleListener('pane-1', 'First');
    titleListener('pane-1', 'Latest');
    useTerminalStore.setState({ terminals: new Map([['pane-1', pane()]]) });

    expect(getTerminalDisplayTitle(useTerminalStore.getState().terminals.get('pane-1'))).toBe('Latest');
    unsubscribe();
  });

  test('restores the latest title after a store action writes back an older pane snapshot', () => {
    const unsubscribe = subscribeToExternalTitles();
    const stale = useTerminalStore.getState().terminals;
    titleListener('pane-1', 'Live');
    useTerminalStore.setState({ terminals: new Map(stale) });

    expect(getTerminalDisplayTitle(useTerminalStore.getState().terminals.get('pane-1'))).toBe('Live');
    unsubscribe();
  });

  test('a title arriving while createTerminal awaits the PTY is kept', async () => {
    let finishPty!: () => void;
    (window as any).terminalAPI.createPty = vi.fn(() => new Promise((resolve) => { finishPty = () => resolve({ pid: 1 }); }));
    useTerminalStore.setState({
      config: { defaultShellId: 'shell', shells: [{ id: 'shell', name: 'Shell', path: 'sh', args: [] }] } as any,
    });
    const creating = useTerminalStore.getState().createTerminal();
    useTerminalStore.getState().setExternalTitle('pane-1', 'During create');
    finishPty();
    await creating;

    expect(useTerminalStore.getState().terminals.size).toBe(2);
    expect(getTerminalDisplayTitle(useTerminalStore.getState().terminals.get('pane-1'))).toBe('During create');
  });

  test('drops an early title if its PTY exits before the pane is inserted', () => {
    useTerminalStore.setState({ terminals: new Map() });
    const unsubscribe = subscribeToExternalTitles();
    titleListener('pane-1', 'Temporary');
    exitListener('pane-1');
    useTerminalStore.setState({ terminals: new Map([['pane-1', pane()]]) });

    expect(getTerminalDisplayTitle(useTerminalStore.getState().terminals.get('pane-1'))).toBe('Shell');
    unsubscribe();
  });

  test('detached windows show the current file title and restore their latest shell title', async () => {
    getPtyExternalTitle.mockResolvedValue('Existing');
    const update = vi.fn();
    const observer = observeDetachedTitle('pane-1', update);
    await Promise.resolve();
    expect(update).toHaveBeenLastCalledWith('Existing', true);
    observer.setBaseTitle('New shell title');
    expect(update).toHaveBeenLastCalledWith('Existing', true);
    titleListener('pane-1', '');
    expect(update).toHaveBeenLastCalledWith('New shell title', false);
    observer.dispose();
  });

  test('a detached window ignores an older initial title after a live update', async () => {
    let resolveTitle!: (title: string) => void;
    getPtyExternalTitle.mockImplementation(() => new Promise((resolve) => { resolveTitle = resolve; }));
    const update = vi.fn();
    const observer = observeDetachedTitle('pane-1', update);
    titleListener('pane-1', 'Newest');
    resolveTitle('Older');
    await Promise.resolve();
    expect(update).toHaveBeenLastCalledWith('Newest', true);
    observer.dispose();
  });
});
