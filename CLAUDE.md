# Notes for coding agents

- tmax.net must run on Windows, macOS and Linux. Check `OperatingSystem.IsWindows()` etc. before platform-specific code; support `\` and `/` paths; get the home dir from `Environment.SpecialFolder.UserProfile`.
- Keyboard shortcuts in the frontend: `isMac ? event.metaKey : event.ctrlKey`, never bare `ctrlKey`.
- No dependency that needs a native compiler at install/build time (no node-gyp, no Native AOT). That is the reason this port exists.
- Keep `Tmax.Core` free of UI/transport code: the WebSocket RPC and the future MCP server both sit on top of it.
- RPC channel names mirror `../tmax/src/shared/ipc-channels.ts` so the original renderer can be ported with minimal changes.
- Never weaken the backend auth (loopback bind, per-launch token, Origin check).
- Verify with `dotnet build`, `dotnet test` and `cd frontend && npm run test:rpc`.
- After adding/updating npm or NuGet dependencies, regenerate THIRD-PARTY-NOTICES.md: `node scripts/generate-third-party-notices.mjs` (after `dotnet build`).
