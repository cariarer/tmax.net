# tmax.net

Port of [tmax](https://github.com/cariarer/tmax) from Electron to **C# (.NET 10) + React**.
Runs on Windows, macOS and Linux. Building needs only the .NET SDK and Node.js — no Visual Studio, no node-gyp.

## Architecture

```
tmax (.NET, self-contained)
├─ Photino window (WebView2 / WKWebView / WebKitGTK)
│    └─ frontend (Vite build, served from wwwroot)
│         └─ backend.ts ── WebSocket 127.0.0.1:<random>/ws?token=… ──┐
└─ Kestrel (embedded)                                                 ▼
     ├─ RpcServer   invoke / send / event, channel names from tmax's ipc-channels.ts
     └─ Tmax.Core   PtyManager (Porta.Pty: ConPTY / forkpty), later Config, Watcher, Git, …
```

Security: the backend binds to loopback only, requires a per-launch random token (passed in the URL
fragment) and rejects WebSocket upgrades from foreign `Origin`s. An open terminal is code execution.

## Layout

| Path | Content |
|---|---|
| `src/Tmax.Core` | UI-independent services (PTY so far). Also the future home of what the MCP server will use. |
| `src/Tmax.App` | Entry point: Kestrel + RPC + Photino window. `Rpc/PendingChannels.cs` lists the channels whose service is not ported yet |
| `frontend/src/renderer`, `frontend/src/shared` | The original tmax React renderer, copied with minimal changes (marked `tmax.net:`) |
| `frontend/src/bridge` | `window.terminalAPI` on top of the WebSocket (same surface as the Electron preload), clipboard, context menu |
| `tests/Tmax.Core.Tests` | xUnit tests for the core services |

## Build & run

Requirements: .NET SDK 10 (see `global.json`), Node.js 20+.

```sh
dotnet build                       # also builds the frontend into wwwroot
dotnet run --project src/Tmax.App  # opens the window
dotnet test                        # core tests
cd frontend && npm test            # renderer unit tests (vitest)
cd frontend && npm run test:rpc    # end-to-end smoke test over the real WebSocket protocol
```

Frontend development with hot reload:

```sh
cd frontend && npm run dev                                   # Vite on :5995
TMAX_DEV_URL=http://localhost:5995 dotnet run --project src/Tmax.App -p:SkipFrontend=true
```

`--headless` starts only the backend and prints `TMAX_UI_URL=…`, which can be opened in a browser.

Release builds: `dotnet publish src/Tmax.App -c Release -r <win-x64|osx-arm64|linux-x64> --self-contained`.
Do not enable Native AOT — on Windows it requires the MSVC linker (Visual Studio).

Linux needs `libwebkit2gtk-4.1` at runtime.

Data lives in `~/Library/Application Support/tmax.net` (macOS), `%APPDATA%\tmax.net` (Windows) or
`~/.config/tmax.net` (Linux); override with `TMAX_DATA_DIR`. `--no-restore` starts without the saved session.

### Differences to the Electron version

- Clipboard: the WebView can read the clipboard synchronously only inside a `paste` event.
  Ctrl/Cmd+V and the context menu use `terminalAPI.clipboardSnapshot()` (async) instead.
- The terminal context menu is HTML instead of a native menu.
- File delete uses `/usr/bin/trash` (macOS 15+) or Finder, `gio trash` on Linux, the Recycle Bin on Windows.
- Process tree on Windows uses a Toolhelp32 snapshot instead of `wmic`.
- `TMAX_PRINT_URL=1` prints the UI URL (with token) for test scripts; never set it in normal use.
- `npm run typecheck` reports 16 errors that already exist in the original renderer; the build does not run tsc (as in the original).

## Roadmap

1. ✅ Proof of concept: window + backend + one PTY over WebSocket (verified on macOS)
2. ✅ `terminalAPI` shim over the WebSocket, original React renderer running; config + session persistence
3. Port services
   - ✅ PTY (shell integration, title files, process tree, batching), config, session, keybindings.json, diag log
   - ✅ Git diff, worktrees, file explorer, Backlog board, detached terminal/backlog windows, window titles
   - ⏳ Copilot / Claude Code session monitors (+ SQLite, WSL sessions, OS notifications)
   - ⏳ Windows 11 background material (Mica/Acrylic)
   - ❓ Telemetry and update checker (both talk to the upstream author's services)
4. Build & CI for all three platforms, installer/updates
5. Later: MCP server on top of `Tmax.Core`, so agents running in a tmax terminal can control it

## License

MIT, see [LICENSE](LICENSE). Based on tmax.
