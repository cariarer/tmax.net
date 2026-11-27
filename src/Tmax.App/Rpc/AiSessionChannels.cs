using System.Text.Json;
using Tmax.App.Windows;
using Tmax.Core.AiSessions;
using Tmax.Core.Config;
using Tmax.Core.Pty;

namespace Tmax.App.Rpc;

/// <summary>Copilot / Claude Code session channels and their events (original main.ts AI handlers).</summary>
public static class AiSessionChannels
{
    public static void Register(RpcServer rpc, ConfigStore config, SessionStore sessions, PtyManager pty, WindowManager windows, ILogger logger)
    {
        var copilot = new CopilotMonitor();
        var claude = new ClaudeCodeMonitor();
        var wsl = new WslSessionManager();
        var notifier = new SessionNotifier(windows.Notify);

        // ── Notification settings, live from the config ──
        void ApplyConfig()
        {
            notifier.Enabled = config.Get<bool?>("aiSessionNotifications") ?? true;
            notifier.SetExcludeStrings(config.Get<List<string>>("notificationExcludeStrings"));
        }
        ApplyConfig();
        config.Changed += (key, _) => { if (key is "aiSessionNotifications" or "notificationExcludeStrings") ApplyConfig(); };
        // Pane names the user set, so toasts show the same name as the pane (seeded from the saved session).
        try
        {
            notifier.SetNameOverrides(sessions.Load()?["sessionNameOverrides"]?.Deserialize<Dictionary<string, string>>());
        }
        catch (JsonException) { }
        rpc.Handle("session:nameOverridesSync", (a, _) =>
        {
            try { notifier.SetNameOverrides(a.Opt<Dictionary<string, string>>(0)); }
            catch (JsonException) { notifier.SetNameOverrides(null); }
            return Task.CompletedTask;
        });

        // ── Events ──
        void Wire(ISessionMonitorEvents m, string prefix)
        {
            m.SessionAdded += s => rpc.Broadcast($"{prefix}:sessionAdded", s);
            m.SessionUpdated += s =>
            {
                rpc.Broadcast($"{prefix}:sessionUpdated", s);
                notifier.OnSessionUpdated(s);
            };
            m.SessionRemoved += id =>
            {
                notifier.Forget(id);
                rpc.Broadcast($"{prefix}:sessionRemoved", id);
            };
        }
        Wire(copilot, "copilot");
        Wire(claude, "claude-code");
        wsl.CopilotAdded += s => rpc.Broadcast("copilot:sessionAdded", s);
        wsl.CopilotUpdated += s => { rpc.Broadcast("copilot:sessionUpdated", s); notifier.OnSessionUpdated(s); };
        wsl.CopilotRemoved += id => rpc.Broadcast("copilot:sessionRemoved", id);
        wsl.ClaudeAdded += s => rpc.Broadcast("claude-code:sessionAdded", s);
        wsl.ClaudeUpdated += s => { rpc.Broadcast("claude-code:sessionUpdated", s); notifier.OnSessionUpdated(s); };
        wsl.ClaudeRemoved += id => rpc.Broadcast("claude-code:sessionRemoved", id);

        int LoadLimit() => config.Get<int?>("aiSessionLoadLimit") is >= 0 and var n ? n : 314;

        // Watch from startup (the renderer also calls start*Watching; both are idempotent, TASK-143).
        _ = Task.Run(() =>
        {
            try { claude.StartWatching(); copilot.StartWatching(); }
            catch (Exception ex) { logger.LogWarning(ex, "Starting AI session watchers failed"); }
        });
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() => wsl.StartAsync(LoadLimit()));
            pty.WslActiveChanged += active => _ = active ? wsl.StartAsync(LoadLimit()) : wsl.StopAsync();
        }

        static List<SessionSummary> Combine(IEnumerable<SessionSummary> a, IEnumerable<SessionSummary> b, int cap) =>
            a.Concat(b).OrderByDescending(s => s.LastActivityTime).Take(cap).ToList();

        // ── Copilot ──
        rpc.Handle("copilot:listSessions", (a, _) => Task.Run<object?>(() =>
        {
            var cap = a.Opt<int?>(0) ?? 50;
            var native = copilot.Scan(cap);
            var fromWsl = wsl.ScanCopilot(cap);
            return new SessionList(Combine(native, fromWsl, cap), copilot.LastTotalEligible + fromWsl.Count, copilot.DbAvailable == true);
        }));
        rpc.Handle("copilot:getSession", (a, _) =>
            Task.FromResult<object?>(copilot.Get(a.Get<string>(0)) ?? wsl.GetCopilot(a.Get<string>(0))));
        rpc.Handle("copilot:searchSessions", (a, _) =>
            Task.Run<object?>(() => copilot.Search(a.Get<string>(0)).Concat(wsl.SearchCopilot(a.Get<string>(0))).ToList()));
        rpc.Handle("copilot:searchPrompts", (a, _) => Task.Run<object?>(() => copilot.Db.SearchPrompts(a.Get<string>(0))));
        rpc.Handle("copilot:getRecentPrompts", (a, _) => Task.Run<object?>(() => copilot.Db.RecentPrompts(a.Opt<int?>(0) ?? 300)));
        rpc.Handle("copilot:startWatching", (_, _) => { copilot.StartWatching(); return Task.CompletedTask; });
        rpc.Handle("copilot:stopWatching", (_, _) => { copilot.StopWatching(); return Task.CompletedTask; });
        rpc.Handle("copilot:getPrompts", (a, _) => Task.Run<object?>(() =>
        {
            var id = a.Get<string>(0);
            var native = copilot.GetPrompts(id, 500);
            return native.Count > 0 ? native : wsl.CopilotPrompts(id, 500);
        }));

        // ── Claude Code ──
        rpc.Handle("claude-code:listSessions", (a, _) => Task.Run<object?>(() =>
        {
            var cap = a.Opt<int?>(0) ?? 50;
            var native = claude.Scan(cap);
            var fromWsl = wsl.ScanClaude(cap);
            return new SessionList(Combine(native, fromWsl, cap), claude.LastTotalEligible + fromWsl.Count);
        }));
        rpc.Handle("claude-code:getSession", (a, _) =>
            Task.FromResult<object?>(claude.Get(a.Get<string>(0)) ?? wsl.GetClaude(a.Get<string>(0))));
        rpc.Handle("claude-code:searchSessions", (a, _) =>
            Task.Run<object?>(() => claude.Search(a.Get<string>(0)).Concat(wsl.SearchClaude(a.Get<string>(0))).ToList()));
        rpc.Handle("claude-code:startWatching", (_, _) => { claude.StartWatching(); return Task.CompletedTask; });
        rpc.Handle("claude-code:stopWatching", (_, _) => { claude.StopWatching(); return Task.CompletedTask; });
        rpc.Handle("claude-code:getPrompts", (a, _) => Task.Run<object?>(() =>
        {
            var id = a.Get<string>(0);
            var native = claude.GetPrompts(id, 500);
            return native.Count > 0 ? native : wsl.ClaudePrompts(id, 500);
        }));

        // ── Shared ──
        rpc.Handle("ai:invalidateCaches", (_, _) => { copilot.InvalidateCache(); claude.InvalidateCache(); return Task.CompletedTask; });
        rpc.Handle("ai:getSessionTimeline", (a, _) => Task.Run<object?>(() =>
            a.Get<string>(0) == "claude-code" ? claude.GetTranscript(a.Get<string>(1)) : copilot.GetTranscript(a.Get<string>(1))));
    }
}
