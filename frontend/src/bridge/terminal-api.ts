/**
 * `window.terminalAPI` on top of the WebSocket backend.
 *
 * Same surface as the original Electron preload (tmax/src/preload/preload.ts), so the
 * renderer code stays as it was. Only differences:
 *  - clipboard reads are synchronous only inside paste events; `clipboardSnapshot()`
 *    is the async variant for keyboard shortcuts and menus (see bridge/clipboard.ts);
 *  - the context menu and detached windows are rendered/handled by the frontend shell
 *    instead of Electron (see README: not ported yet ones answer with safe defaults).
 */
import { IPC } from '../shared/ipc-channels';
import type { DiffMode, DiffResult, AnnotatedFile } from '../shared/diff-types';
import type { RepoWorktrees } from '../shared/worktree-types';
import type { BacklogTask } from '../shared/backlog-types';
import type { Backend } from '../backend';
import * as clip from './clipboard';
import type { ClipboardSnapshot } from './clipboard';

export interface PtyDiag {
  pid: number;
  writeCount: number;
  lastWriteTime: number;
  dataCount: number;
  lastDataTime: number;
  dataBytes: number;
}

type UpdateInfo = { status: string; current: string; latest?: string; url?: string; error?: string; releaseNotes?: string };
type Keybinding = { key: string; action: string };

export interface TerminalAPI {
  createPty(opts: {
    id: string;
    shellPath: string;
    args: string[];
    cwd: string;
    env?: Record<string, string>;
    cols: number;
    rows: number;
    wslDistro?: string;
  }): Promise<{ id: string; pid: number }>;
  writePty(id: string, data: string): void;
  resizePty(id: string, cols: number, rows: number): Promise<void>;
  killPty(id: string): Promise<void>;
  onPtyData(cb: (id: string, data: string) => void): () => void;
  onPtyExit(cb: (id: string, exitCode: number | undefined) => void): () => void;
  onPtyExternalTitle(cb: (id: string, title: string) => void): () => void;
  getPtyExternalTitle(id: string): Promise<string>;
  disablePtyExternalTitle(id: string): void;
  getConfig(): Promise<Record<string, unknown>>;
  setConfig(key: string, value: unknown): Promise<void>;
  clipboardRead(): string;
  clipboardReadHTML(): string;
  clipboardWrite(text: string): void;
  clipboardHasImage(): boolean;
  clipboardSaveImage(): Promise<string>;
  /** tmax.net: async clipboard read for code paths outside a paste event. */
  clipboardSnapshot(): Promise<{ hasImage: boolean; html: string; plainText: string }>;
  imageReadAsDataUrl(filePath: string): Promise<string | null>;
  resolveClipboardImageBasename(basename: string): Promise<string | null>;
  getAppVersion(): Promise<string>;
  getVersionUpdate(): Promise<UpdateInfo | null>;
  checkForUpdates(): void;
  restartAndUpdate(): void;
  getChangelog(): Promise<string>;
  onUpdateStatusChanged(cb: (info: UpdateInfo) => void): () => void;
  getPtyDiag(id: string): Promise<PtyDiag | null>;
  diagLog(event: string, data?: Record<string, unknown>): void;
  getDiagLogPath(): Promise<string>;
  readDiagLogTail(maxBytes?: number): Promise<string>;
  showTerminalContextMenu(): Promise<string | null>;
  getSystemFonts(): Promise<string[]>;
  getKeybindings(): Promise<Keybinding[]>;
  openKeybindingsFile(): Promise<void>;
  openConfigFile(): Promise<void>;
  resetKeybindings(): Promise<Keybinding[]>;
  onKeybindingsChanged(cb: (bindings: Keybinding[]) => void): () => void;
  setBackgroundMaterial(material: string): Promise<void>;
  getPlatformSupportsMaterial(): Promise<boolean>;
  diffResolveGitRoot(cwd: string): Promise<string>;
  diffGetDiff(cwd: string, mode: DiffMode): Promise<DiffResult>;
  diffGetAnnotatedFile(cwd: string, filePath: string, mode: DiffMode): Promise<AnnotatedFile>;
  fileList(dirPath: string, wslDistro?: string): Promise<{ name: string; isDirectory: boolean; path: string }[]>;
  fileRead(filePath: string, wslDistro?: string): Promise<string | null>;
  fileReveal(filePath: string, wslDistro?: string): Promise<{ ok: boolean; error?: string }>;
  fileRename(oldPath: string, newName: string, wslDistro?: string): Promise<{ ok: boolean; newPath?: string; error?: string }>;
  fileDelete(filePath: string, wslDistro?: string): Promise<{ ok: boolean; error?: string }>;
  listWorktrees(cwd: string): Promise<RepoWorktrees>;
  createWorktree(repoPath: string, branchName: string, baseBranch: string): Promise<{ success: boolean; worktreePath?: string; error?: string }>;
  deleteWorktree(repoPath: string, worktreePath: string): Promise<{ success: boolean; error?: string }>;
  getBranches(repoPath: string): Promise<string[]>;
  syncSessionNameOverrides(overrides: Record<string, string>): void;
  onSessionFileChanged(cb: () => void): () => void;
  openBacklogWindow(): Promise<void>;
  closeBacklogWindow(): Promise<void>;
  onBacklogWindowClosed(cb: () => void): () => void;
  getPtyChildProcesses(ptyId: string): Promise<string[]>;
  backlogListTasks(projects: { name: string; path: string }[], includeArchived?: boolean): Promise<BacklogTask[]>;
  backlogGetTask(projectPath: string, sub: string, file: string): Promise<{ frontmatter: Record<string, unknown>; body: string } | null>;
  backlogEditTask(payload: { projectPath: string; taskId: string; status?: string; title?: string; description?: string; checkAc?: number[]; uncheckAc?: number[] }): Promise<{ ok: boolean; error?: string }>;
  backlogCreateTask(payload: { projectPath: string; title: string; status?: string; description?: string; labels?: string[] }): Promise<{ ok: boolean; id?: string; error?: string }>;
  backlogArchiveTask(projectPath: string, taskId: string): Promise<{ ok: boolean; error?: string }>;
  backlogDeleteTask(projectPath: string, taskId: string): Promise<{ ok: boolean; error?: string }>;
  backlogValidateProject(projectPath: string): Promise<{ ok: boolean }>;
  backlogInitProject(projectPath: string, name: string): Promise<{ ok: boolean; error?: string }>;
  backlogPickFolder(defaultPath?: string): Promise<string | null>;
  backlogSaveImage(projectPath: string): Promise<{ ok: boolean; relPath?: string; error?: string }>;
  // Present in the original preload object but missing from its interface.
  openPath(filePath: string): Promise<unknown>;
  saveSession(data: unknown): Promise<void>;
  loadSession(): Promise<unknown>;
  detachTerminal(id: string): Promise<unknown>;
  closeDetached(id: string): Promise<unknown>;
  focusDetached(id: string): Promise<unknown>;
  onDetachedClosed(cb: (id: string) => void): () => void;
}

export interface PlatformInfo {
  platform: 'win32' | 'darwin' | 'linux';
  homeDir: string;
  isDev: boolean;
  windowsBuildNumber: number;
}

export function createTerminalAPI(backend: Backend, showContextMenu: () => Promise<string | null>) {
  const inv = <T>(channel: string, ...args: unknown[]) => backend.invoke<T>(channel, ...args);
  const send = (channel: string, ...args: unknown[]) => backend.send(channel, ...args);
  const on = (channel: string, cb: (...args: any[]) => void) => backend.on(channel, cb);

  /** Image found by the last clipboard read (sync paste event or async snapshot). */
  const pendingImage = (): Blob | null => clip.syncSnapshot().image;

  const api: TerminalAPI & Record<string, unknown> = {
    // ── PTY ──────────────────────────────────────────────────────────
    createPty: (opts) => inv(IPC.PTY_CREATE, opts),
    writePty: (id, data) => send(IPC.PTY_WRITE, id, data),
    resizePty: (id, cols, rows) => inv(IPC.PTY_RESIZE, id, cols, rows),
    killPty: (id) => inv(IPC.PTY_KILL, id),
    onPtyData: (cb) => on(IPC.PTY_DATA, cb),
    onPtyExit: (cb) => on(IPC.PTY_EXIT, (id: string, code: number | null) => cb(id, code ?? undefined)),
    onPtyExternalTitle: (cb) => on(IPC.PTY_EXTERNAL_TITLE, cb),
    getPtyExternalTitle: (id) => inv(IPC.PTY_GET_EXTERNAL_TITLE, id),
    disablePtyExternalTitle: (id) => send(IPC.PTY_DISABLE_EXTERNAL_TITLE, id),
    getPtyDiag: (id) => inv(IPC.PTY_GET_DIAG, id),
    getPtyChildProcesses: (id) => inv(IPC.PTY_GET_CHILD_PROCESSES, id),

    // ── Config / keybindings ─────────────────────────────────────────
    getConfig: () => inv(IPC.CONFIG_GET),
    setConfig: (key, value) => inv(IPC.CONFIG_SET, key, value),
    openConfigFile: () => inv(IPC.CONFIG_OPEN),
    getKeybindings: () => inv(IPC.KEYBINDINGS_GET),
    openKeybindingsFile: () => inv(IPC.KEYBINDINGS_OPEN_FILE),
    resetKeybindings: () => inv(IPC.KEYBINDINGS_RESET),
    onKeybindingsChanged: (cb) => on(IPC.KEYBINDINGS_CHANGED, cb),

    // ── Clipboard ────────────────────────────────────────────────────
    clipboardRead: () => clip.syncSnapshot().plainText,
    clipboardReadHTML: () => clip.syncSnapshot().html,
    clipboardHasImage: () => clip.syncSnapshot().image !== null,
    clipboardWrite: (text) => clip.writeText(text),
    async clipboardSnapshot() {
      const s: ClipboardSnapshot = await clip.readSnapshot();
      return { hasImage: s.image !== null, html: s.html, plainText: s.plainText };
    },
    async clipboardSaveImage() {
      const image = pendingImage() ?? (await clip.readSnapshot()).image;
      if (!image) return '';
      return inv<string>(IPC.CLIPBOARD_SAVE_IMAGE, await clip.blobToBase64(image));
    },
    imageReadAsDataUrl: (filePath) => inv(IPC.IMAGE_READ_DATA_URL, filePath),
    resolveClipboardImageBasename: (basename) => inv(IPC.RESOLVE_CLIPBOARD_BASENAME, basename),

    // ── Shell / session ──────────────────────────────────────────────
    openPath: (filePath: string) => inv(IPC.OPEN_PATH, filePath),
    saveSession: (data: unknown) => inv(IPC.SESSION_SAVE, data),
    loadSession: () => inv(IPC.SESSION_LOAD),
    syncSessionNameOverrides: (overrides) => send(IPC.SESSION_NAME_OVERRIDES_SYNC, overrides),
    onSessionFileChanged: (cb) => on(IPC.SESSION_FILE_CHANGED, cb),
    showTerminalContextMenu: () => showContextMenu(),
    getSystemFonts: () => inv(IPC.GET_SYSTEM_FONTS),

    // ── Windows ──────────────────────────────────────────────────────
    detachTerminal: (id: string) => inv(IPC.DETACH_CREATE, id),
    closeDetached: (id: string) => inv(IPC.DETACH_CLOSE, id),
    focusDetached: (id: string) => inv(IPC.DETACH_FOCUS, id),
    onDetachedClosed: (cb: (id: string) => void) => on(IPC.DETACH_CLOSED, cb),
    openBacklogWindow: () => inv(IPC.BACKLOG_WINDOW_OPEN),
    closeBacklogWindow: () => inv(IPC.BACKLOG_WINDOW_CLOSE),
    onBacklogWindowClosed: (cb) => on(IPC.BACKLOG_WINDOW_CLOSED, cb),
    setBackgroundMaterial: (material) => inv(IPC.SET_BACKGROUND_MATERIAL, material),
    getPlatformSupportsMaterial: () => inv(IPC.GET_PLATFORM_SUPPORTS_MATERIAL),

    // ── Copilot / Claude Code sessions ───────────────────────────────
    listCopilotSessions: (limit?: number) => inv(IPC.COPILOT_LIST_SESSIONS, limit),
    getCopilotSession: (id: string) => inv(IPC.COPILOT_GET_SESSION, id),
    searchCopilotSessions: (query: string) => inv(IPC.COPILOT_SEARCH_SESSIONS, query),
    startCopilotWatching: () => inv(IPC.COPILOT_START_WATCHING),
    stopCopilotWatching: () => inv(IPC.COPILOT_STOP_WATCHING),
    getCopilotPrompts: (id: string) => inv(IPC.COPILOT_GET_PROMPTS, id),
    searchCopilotPrompts: (query: string) => inv(IPC.COPILOT_SEARCH_PROMPTS, query),
    getCopilotRecentPrompts: (limit?: number) => inv(IPC.COPILOT_GET_RECENT_PROMPTS, limit),
    invalidateSessionCaches: () => inv(IPC.AI_INVALIDATE_CACHES),
    onCopilotSessionUpdated: (cb: (s: unknown) => void) => on(IPC.COPILOT_SESSION_UPDATED, cb),
    onCopilotSessionAdded: (cb: (s: unknown) => void) => on(IPC.COPILOT_SESSION_ADDED, cb),
    onCopilotSessionRemoved: (cb: (id: string) => void) => on(IPC.COPILOT_SESSION_REMOVED, cb),
    listClaudeCodeSessions: (limit?: number) => inv(IPC.CLAUDE_CODE_LIST_SESSIONS, limit),
    getClaudeCodeSession: (id: string) => inv(IPC.CLAUDE_CODE_GET_SESSION, id),
    searchClaudeCodeSessions: (query: string) => inv(IPC.CLAUDE_CODE_SEARCH_SESSIONS, query),
    startClaudeCodeWatching: () => inv(IPC.CLAUDE_CODE_START_WATCHING),
    stopClaudeCodeWatching: () => inv(IPC.CLAUDE_CODE_STOP_WATCHING),
    getClaudeCodePrompts: (id: string) => inv(IPC.CLAUDE_CODE_GET_PROMPTS, id),
    getSessionTimeline: (provider: 'copilot' | 'claude-code', id: string) => inv(IPC.AI_GET_SESSION_TIMELINE, provider, id),
    onClaudeCodeSessionUpdated: (cb: (s: unknown) => void) => on(IPC.CLAUDE_CODE_SESSION_UPDATED, cb),
    onClaudeCodeSessionAdded: (cb: (s: unknown) => void) => on(IPC.CLAUDE_CODE_SESSION_ADDED, cb),
    onClaudeCodeSessionRemoved: (cb: (id: string) => void) => on(IPC.CLAUDE_CODE_SESSION_REMOVED, cb),

    // ── Version / diagnostics ────────────────────────────────────────
    getAppVersion: () => inv(IPC.VERSION_GET_APP_VERSION),
    getVersionUpdate: () => inv(IPC.VERSION_GET_UPDATE),
    checkForUpdates: () => send(IPC.VERSION_CHECK_NOW),
    restartAndUpdate: () => send(IPC.VERSION_RESTART_AND_UPDATE),
    getChangelog: () => inv(IPC.VERSION_GET_CHANGELOG),
    onUpdateStatusChanged: (cb) => on(IPC.VERSION_UPDATE_STATUS, cb),
    diagLog: (event, data) => send(IPC.DIAG_LOG, event, data),
    getDiagLogPath: () => inv(IPC.DIAG_GET_LOG_PATH),
    readDiagLogTail: (maxBytes) => inv(IPC.DIAG_READ_TAIL, maxBytes),

    // ── Diff / files / git ───────────────────────────────────────────
    diffResolveGitRoot: (cwd) => inv(IPC.DIFF_RESOLVE_GIT_ROOT, cwd),
    diffGetDiff: (cwd, mode) => inv(IPC.DIFF_GET_DIFF, cwd, mode),
    diffGetAnnotatedFile: (cwd, filePath, mode) => inv(IPC.DIFF_GET_ANNOTATED_FILE, cwd, filePath, mode),
    fileList: (dirPath, wslDistro) => inv(IPC.FILE_LIST, dirPath, wslDistro),
    fileRead: (filePath, wslDistro) => inv(IPC.FILE_READ, filePath, wslDistro),
    fileReveal: (filePath, wslDistro) => inv(IPC.FILE_REVEAL, filePath, wslDistro),
    fileRename: (oldPath, newName, wslDistro) => inv(IPC.FILE_RENAME, oldPath, newName, wslDistro),
    fileDelete: (filePath, wslDistro) => inv(IPC.FILE_DELETE, filePath, wslDistro),
    listWorktrees: (cwd) => inv(IPC.GIT_LIST_WORKTREES, cwd),
    createWorktree: (repoPath, branchName, baseBranch) => inv(IPC.GIT_CREATE_WORKTREE, repoPath, branchName, baseBranch),
    deleteWorktree: (repoPath, worktreePath) => inv(IPC.GIT_DELETE_WORKTREE, repoPath, worktreePath),
    getBranches: (repoPath) => inv(IPC.GIT_GET_BRANCHES, repoPath),

    // ── Backlog ──────────────────────────────────────────────────────
    backlogListTasks: (projects, includeArchived) => inv(IPC.BACKLOG_LIST_TASKS, projects, includeArchived),
    backlogGetTask: (projectPath, sub, file) => inv(IPC.BACKLOG_GET_TASK, projectPath, sub, file),
    backlogEditTask: (payload) => inv(IPC.BACKLOG_EDIT_TASK, payload),
    backlogCreateTask: (payload) => inv(IPC.BACKLOG_CREATE_TASK, payload),
    backlogArchiveTask: (projectPath, taskId) => inv(IPC.BACKLOG_ARCHIVE_TASK, projectPath, taskId),
    backlogDeleteTask: (projectPath, taskId) => inv(IPC.BACKLOG_DELETE_TASK, projectPath, taskId),
    backlogValidateProject: (projectPath) => inv(IPC.BACKLOG_VALIDATE_PROJECT, projectPath),
    backlogInitProject: (projectPath, name) => inv(IPC.BACKLOG_INIT_PROJECT, projectPath, name),
    backlogPickFolder: (defaultPath) => inv(IPC.BACKLOG_PICK_FOLDER, defaultPath),
    async backlogSaveImage(projectPath) {
      const image = pendingImage() ?? (await clip.readSnapshot()).image;
      if (!image) return { ok: false, error: 'No image on the clipboard' };
      return inv(IPC.BACKLOG_SAVE_IMAGE, projectPath, await clip.blobToBase64(image));
    },
  };
  return api;
}
