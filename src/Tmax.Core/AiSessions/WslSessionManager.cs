using Tmax.Core.Platform;

namespace Tmax.Core.AiSessions;

/// <summary>
/// Copilot / Claude Code monitors for every WSL distro (port of src/main/wsl-session-manager.ts).
/// Started while WSL terminals are open: its pollers over //wsl.localhost would otherwise keep the
/// WSL VM (vmmemWSL) alive all the time (TASK-158).
/// </summary>
public sealed class WslSessionManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<(CopilotMonitor Copilot, ClaudeCodeMonitor Claude)> _pairs = [];

    public event Action<SessionSummary>? CopilotAdded, CopilotUpdated, ClaudeAdded, ClaudeUpdated;
    public event Action<string>? CopilotRemoved, ClaudeRemoved;

    public bool Started { get; private set; }

    public async Task StartAsync(int initialLimit)
    {
        if (!WslDistros.IsAvailable) return;
        await _gate.WaitAsync();
        try
        {
            if (Started) return;
            var pairs = new List<(CopilotMonitor, ClaudeCodeMonitor)>();
            foreach (var d in await WslDistros.GetAsync())
            {
                var copilot = new CopilotMonitor(d.CopilotBasePath, d.Name);
                copilot.SessionAdded += s => CopilotAdded?.Invoke(s);
                copilot.SessionUpdated += s => CopilotUpdated?.Invoke(s);
                copilot.SessionRemoved += id => CopilotRemoved?.Invoke(id);
                var claude = new ClaudeCodeMonitor(d.ClaudeBasePath, d.Name);
                claude.SessionAdded += s => ClaudeAdded?.Invoke(s);
                claude.SessionUpdated += s => ClaudeUpdated?.Invoke(s);
                claude.SessionRemoved += id => ClaudeRemoved?.Invoke(id);
                copilot.StartWatching();
                claude.StartWatching();
                if (initialLimit > 0)
                {
                    await Task.Run(() => { copilot.Scan(initialLimit); claude.Scan(initialLimit); });
                }
                pairs.Add((copilot, claude));
            }
            _pairs = pairs;
            Started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var (copilot, claude) in _pairs) { copilot.Dispose(); claude.Dispose(); }
            _pairs = [];
            Started = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<(CopilotMonitor Copilot, ClaudeCodeMonitor Claude)> Pairs => _pairs;

    public List<SessionSummary> ScanCopilot(int limit) => Pairs.SelectMany(p => p.Copilot.Scan(limit)).ToList();
    public List<SessionSummary> ScanClaude(int limit) => Pairs.SelectMany(p => p.Claude.Scan(limit)).ToList();
    public CopilotSession? GetCopilot(string id) => Pairs.Select(p => p.Copilot.Get(id)).FirstOrDefault(s => s is not null);
    public SessionSummary? GetClaude(string id) => Pairs.Select(p => p.Claude.Get(id)).FirstOrDefault(s => s is not null);
    public List<SessionSummary> SearchCopilot(string q) => Pairs.SelectMany(p => p.Copilot.Search(q)).ToList();
    public List<SessionSummary> SearchClaude(string q) => Pairs.SelectMany(p => p.Claude.Search(q)).ToList();

    public List<string> CopilotPrompts(string id, int limit) =>
        Pairs.FirstOrDefault(p => p.Copilot.Get(id) is not null).Copilot?.GetPrompts(id, limit) ?? [];

    public List<string> ClaudePrompts(string id, int limit) =>
        Pairs.FirstOrDefault(p => p.Claude.Get(id) is not null).Claude?.GetPrompts(id, limit) ?? [];

    public async ValueTask DisposeAsync() => await StopAsync();
}
