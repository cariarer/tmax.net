# tmax.net

Port of [tmax](https://github.com/InbarR/tmax) by Inbar Rotem from Electron to **C# (.NET 10) + React**.
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

## Releases and updates

Push a tag `vX.Y.Z` (or run the *Release* workflow manually with a version). `.github/workflows/release.yml`
builds and tests on each platform, publishes self-contained apps for win-x64, win-arm64, osx-arm64,
osx-x64 and linux-x64, packs them with [Velopack](https://velopack.io) (one update channel per
platform, delta updates from the previous release) and uploads everything into a single GitHub
release, which is published after the last upload. Per platform you get a setup
(`tmax.net-<channel>-Setup.exe` / `.pkg` / `.AppImage`), a portable zip and the update packages.
The build bakes the repository URL into the app (`-p:TmaxUpdateRepo=…`), so installed copies update
themselves from this repo's releases. Dev builds never update. There is no telemetry. Installers are
not code-signed yet.

```sh
git tag v0.2.0 && git push origin v0.2.0
```

`.github/workflows/ci.yml` builds and runs all tests (xUnit, vitest, RPC smoke test) on Windows, macOS and Linux.

## Status

All IPC channels of the original are implemented (see `src/Tmax.App/Rpc/*Channels.cs`):
PTY (shell integration, `TMAX_TITLE_FILE`, process tree, batching), config, session, keybindings.json,
diagnostics log, git diff, worktrees, file explorer, Backlog board, detached terminal/backlog windows,
Copilot CLI and Claude Code session monitors (SQLite, WSL distros, OS notifications), Windows 11
backdrop materials, auto-update.

Known gaps:
- Clicking an OS notification does not focus tmax (Photino has no click callback).
- Installers are not code-signed: macOS Gatekeeper and Windows SmartScreen will warn on first start.
- Turning on a Windows backdrop material from "none" takes effect after a restart (WebView2 limitation).
- The native window was verified on macOS only; Windows/Linux are covered by CI builds and headless tests.

Next: an MCP server on top of `Tmax.Core`, so agents running in a tmax pane can drive tmax.

## Credits and license

tmax.net is based on [tmax](https://github.com/InbarR/tmax) by Inbar Rotem and contributors, licensed
under MIT. The React UI (`frontend/src/renderer`, `frontend/src/shared`) is taken from tmax largely
unchanged; the backend was rewritten in C# following the original's behaviour.

tmax.net is licensed under the MIT License, see [LICENSE](LICENSE). Third-party components shipped
with the app and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
(regenerate with `node scripts/generate-third-party-notices.mjs` after dependency updates).
