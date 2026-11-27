using System.Text.RegularExpressions;

namespace Tmax.Core.AiSessions;

/// <summary>
/// Copilot CLI session list (port of src/main/copilot-session-monitor.ts). Uses the CLI's SQLite
/// store when present (instant, all sessions queryable), else scans ~/.copilot/session-state/*/.
/// </summary>
public sealed partial class CopilotMonitor : ISessionMonitorEvents, IDisposable
{
    private const int MaxCandidates = 1000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, CopilotSession> _sessions = [];
    private readonly string? _wslDistro;
    private readonly CopilotSessionDb _db;
    private List<(string Id, double Mtime)>? _candidates;
    private SessionFileWatcher? _watcher;

    public CopilotMonitor(string? basePath = null, string? wslDistro = null)
    {
        BasePath = basePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");
        _wslDistro = wslDistro;
        _db = new CopilotSessionDb(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(BasePath))!, "session-store.db"));
    }

    public string BasePath { get; }
    public int LastTotalEligible { get; private set; }
    public bool? DbAvailable { get; private set; }
    public CopilotSessionDb Db => _db;

    public event Action<SessionSummary>? SessionAdded;
    public event Action<SessionSummary>? SessionUpdated;
    public event Action<string>? SessionRemoved;

    private string EventsFile(string id) => Path.Combine(BasePath, id, "events.jsonl");

    // ── Scan ─────────────────────────────────────────────────────────

    public List<SessionSummary> Scan(int limit = 314)
    {
        lock (_lock) DbAvailable ??= _db.TryOpen();
        if (DbAvailable == true)
        {
            var viaDb = ScanWithDb(limit);
            if (viaDb is not null) return viaDb;
            DbAvailable = false;
        }
        return ScanFilesystem(limit);
    }

    private List<SessionSummary>? ScanWithDb(int limit)
    {
        var rows = _db.QuerySessions(limit);
        if (rows is null) return null;
        var total = _db.TotalEligibleCount();
        if (rows.Count == 0)
        {
            LastTotalEligible = total ?? 0;
            return [];
        }
        var stats = _db.QueryTurnStats(rows.Select(r => r.Id).ToList());
        if (stats is null) return null;
        if (total is not null) LastTotalEligible = total.Value;

        var summaries = new List<SessionSummary>();
        var added = new List<SessionSummary>();
        lock (_lock)
        {
            var current = new HashSet<string>();
            foreach (var row in rows)
            {
                current.Add(row.Id);
                var summary = WithWsl(CopilotSessionDb.ToSummary(row, stats.GetValueOrDefault(row.Id)));
                summaries.Add(summary);
                if (_sessions.TryAdd(row.Id, FromDbSummary(row, summary))) added.Add(summary);
            }
            foreach (var id in _sessions.Keys.Where(id => !current.Contains(id)).ToList())
            {
                _sessions.Remove(id);
                CopilotParser.ClearCache(EventsFile(id));
            }
        }
        foreach (var s in added) SessionAdded?.Invoke(s);

        // The DB lags behind events.jsonl: refresh recently active sessions from the event log.
        var recent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5 * 60_000;
        for (var i = 0; i < summaries.Count; i++)
            if (summaries[i].LastActivityTime > recent && Refresh(summaries[i].Id) is { } fresh) summaries[i] = fresh;
        return summaries;
    }

    private static CopilotSession FromDbSummary(CopilotSessionDb.SessionRow row, SessionSummary s) => new()
    {
        Id = row.Id,
        Workspace = new CopilotWorkspace
        {
            Cwd = row.Cwd, Branch = row.Branch, Repository = row.Repository,
            Name = row.Summary.Length > 0 ? row.Summary : row.Id, Summary = row.Summary, CreatedAt = s.CreatedAt,
        },
        MessageCount = s.MessageCount,
        LastActivityTime = s.LastActivityTime,
        FirstPrompt = s.FirstPrompt,
        LatestPrompt = s.LatestPrompt,
        LatestPromptTime = s.LatestPromptTime,
    };

    private List<SessionSummary> ScanFilesystem(int limit)
    {
        var summaries = new List<SessionSummary>();
        var added = new List<SessionSummary>();
        lock (_lock)
        {
            _candidates ??= FindCandidates();
            LastTotalEligible = _candidates.Count;
            var current = new HashSet<string>();
            foreach (var (id, _) in _candidates.Take(limit).ToList())
            {
                current.Add(id);
                if (_sessions.TryGetValue(id, out var known))
                {
                    summaries.Add(ToSummary(known));
                    continue;
                }
                var session = Load(id);
                if (session is null)
                {
                    _candidates.RemoveAll(c => c.Id == id);
                    LastTotalEligible = _candidates.Count;
                    continue;
                }
                _sessions[id] = session;
                var summary = ToSummary(session);
                summaries.Add(summary);
                added.Add(summary);
            }
            foreach (var id in _sessions.Keys.Where(id => !current.Contains(id)).ToList())
            {
                _sessions.Remove(id);
                CopilotParser.ClearCache(EventsFile(id));
            }
        }
        foreach (var s in added) SessionAdded?.Invoke(s);
        return summaries;
    }

    private List<(string, double)> FindCandidates()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        var list = new List<(string, double)>();
        if (!Directory.Exists(BasePath)) return list;
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(BasePath).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (var dir in dirs)
        {
            var events = Path.Combine(dir, "events.jsonl");
            var ws = Path.Combine(dir, "workspace.yaml");
            var file = File.Exists(events) ? events : File.Exists(ws) ? ws : null;
            if (file is null) continue;
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
            if (mtime < cutoff) continue;
            list.Add((Path.GetFileName(dir), mtime.ToUnixTimeMilliseconds()));
        }
        return list.OrderByDescending(c => c.Item2).Take(MaxCandidates).ToList();
    }

    public void InvalidateCache()
    {
        lock (_lock) _candidates = null;
    }

    // ── Accessors ────────────────────────────────────────────────────

    public CopilotSession? Get(string id)
    {
        lock (_lock) return _sessions.GetValueOrDefault(id);
    }

    public List<SessionSummary> Search(string query)
    {
        if (DbAvailable == true && _db.SearchSessions(query, 50) is { } rows)
        {
            var stats = _db.QueryTurnStats(rows.Select(r => r.Id).ToList());
            var results = new List<SessionSummary>();
            lock (_lock)
            {
                foreach (var row in rows)
                {
                    var summary = WithWsl(CopilotSessionDb.ToSummary(row, stats?.GetValueOrDefault(row.Id)));
                    _sessions.TryAdd(row.Id, FromDbSummary(row, summary));
                    results.Add(summary);
                }
            }
            return results;
        }

        var tokens = AndFilter.Tokenize(query);
        if (tokens.Count == 0) return [];
        lock (_lock)
        {
            return _sessions.Values.Where(s => AndFilter.MatchesAll(string.Join('\n', s.Workspace.Repository, s.Workspace.Branch,
                    s.Workspace.Cwd, s.Workspace.Name, s.Workspace.Summary, s.LatestPrompt ?? "", s.Id).ToLowerInvariant(), tokens))
                .Select(ToSummary).ToList();
        }
    }

    public List<string> GetPrompts(string id, int limit = 10) => IsSafeId(id) ? CopilotParser.ExtractPrompts(EventsFile(id), limit) : [];

    public List<TranscriptEntry> GetTranscript(string id) => IsSafeId(id) ? CopilotParser.Transcript(EventsFile(id)) : [];

    /// <summary>Session ids are directory names; never let one escape the base directory.</summary>
    private static bool IsSafeId(string id) => id.Length > 0 && id.IndexOfAny(['/', '\\']) < 0 && id is not ("." or "..");

    // ── Watcher ──────────────────────────────────────────────────────

    public void StartWatching()
    {
        lock (_lock)
        {
            if (_watcher is not null) return;
            _watcher = new SessionFileWatcher(BasePath, n => n is "events.jsonl" or "workspace.yaml");
        }
        _watcher.Added += f => OnFile(f, added: true);
        _watcher.Changed += f => OnFile(f, added: false);
        _watcher.Removed += f =>
        {
            if (Path.GetFileName(f) == "events.jsonl") OnSessionRemoved(SessionIdOf(f));
        };
        _watcher.StaleCheck += RefreshLoaded;
        _watcher.Start();
    }

    public void StopWatching()
    {
        SessionFileWatcher? w;
        lock (_lock) { w = _watcher; _watcher = null; }
        w?.Dispose();
    }

    private static string SessionIdOf(string file) => Path.GetFileName(Path.GetDirectoryName(file)!);

    private readonly HashSet<string> _knownIds = [];

    private void OnFile(string file, bool added)
    {
        var id = SessionIdOf(file);
        bool isNew;
        lock (_lock) isNew = added && _knownIds.Add(id) && !_sessions.ContainsKey(id);
        if (isNew) OnNewSession(id);
        else OnEventsChanged(id);
    }

    public void OnEventsChanged(string id)
    {
        lock (_lock)
        {
            var idx = _candidates?.FindIndex(c => c.Id == id) ?? -1;
            if (idx > 0)
            {
                _candidates!.RemoveAt(idx);
                _candidates.Insert(0, (id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
        }
        Refresh(id);
    }

    public void OnNewSession(string id)
    {
        CopilotSession? session;
        lock (_lock)
        {
            if (_candidates is not null && _candidates.All(c => c.Id != id))
            {
                _candidates.Insert(0, (id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                if (_candidates.Count > MaxCandidates) _candidates.RemoveAt(_candidates.Count - 1);
                LastTotalEligible = _candidates.Count;
            }
            session = Load(id);
            if (session is not null) _sessions[id] = session;
        }
        if (session is not null) SessionAdded?.Invoke(ToSummary(session));
    }

    public void OnSessionRemoved(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _sessions.Remove(id);
            if (removed) CopilotParser.ClearCache(EventsFile(id));
            _knownIds.Remove(id);
            if (_candidates is not null)
            {
                _candidates.RemoveAll(c => c.Id == id);
                LastTotalEligible = _candidates.Count;
            }
        }
        if (removed) SessionRemoved?.Invoke(id);
    }

    public SessionSummary? Refresh(string id)
    {
        if (!IsSafeId(id)) return null;
        if (!Directory.Exists(Path.Combine(BasePath, id)))
        {
            OnSessionRemoved(id);
            return null;
        }
        CopilotSession? session, old;
        lock (_lock)
        {
            session = Load(id);
            if (session is null) return null;
            old = _sessions.GetValueOrDefault(id);
            _sessions[id] = session;
        }
        var summary = ToSummary(session);
        if (old is not null && (old.Status != session.Status || old.MessageCount != session.MessageCount
                                || old.ToolCallCount != session.ToolCallCount || old.LatestPrompt != session.LatestPrompt
                                || old.Workspace.Summary != session.Workspace.Summary || old.Workspace.Name != session.Workspace.Name
                                || old.Workspace.UserNamed != session.Workspace.UserNamed))
            SessionUpdated?.Invoke(summary);
        return summary;
    }

    public void RefreshLoaded()
    {
        List<string> ids;
        lock (_lock) ids = _sessions.Values.Where(s => s.Status != SessionStatus.Idle).Select(s => s.Id).ToList();
        foreach (var id in ids) Refresh(id);
    }

    // ── Loading ──────────────────────────────────────────────────────

    private CopilotSession? Load(string id)
    {
        var dir = Path.Combine(BasePath, id);
        var ws = ParseWorkspace(dir);
        var events = EventsFile(id);
        var parsed = File.Exists(events) ? CopilotParser.Parse(events) : null;
        if (ws.Summary.Length == 0 && CopilotParser.ExtractPrompts(events, 1) is [var first, ..])
        {
            ws.Summary = Json.Clip(first, 60);
            ws.Name = ws.Summary;
        }
        return new CopilotSession
        {
            Id = id,
            Status = parsed?.Status ?? SessionStatus.Idle,
            Workspace = ws,
            MessageCount = parsed?.MessageCount ?? 0,
            ToolCallCount = parsed?.ToolCallCount ?? 0,
            LastActivityTime = parsed?.LastActivityTime ?? 0,
            PendingToolCalls = parsed?.PendingToolCalls ?? 0,
            TotalTokens = parsed?.TotalTokens ?? 0,
            FirstPrompt = parsed?.FirstPrompt is { Length: > 0 } fp ? fp : null,
            LatestPrompt = parsed?.LatestPrompt is { Length: > 0 } lp ? lp : null,
            LatestPromptTime = parsed?.LatestPromptTime is > 0 and var t ? t : null,
        };
    }

    /// <summary>workspace.yaml: flat keys plus folded/literal block scalars (port of parseWorkspace).</summary>
    public static CopilotWorkspace ParseWorkspace(string sessionDir)
    {
        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDir));
        var result = new CopilotWorkspace { Name = dirName };
        var path = Path.Combine(sessionDir, "workspace.yaml");
        if (!File.Exists(path)) return result;
        try
        {
            var lines = File.ReadAllText(path).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var indent = line.Length - line.TrimStart().Length;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = Quotes().Replace(line[(colon + 1)..].Trim(), "");
                if (BlockIndicator().IsMatch(value))
                {
                    var block = new List<string>();
                    while (i + 1 < lines.Length)
                    {
                        var next = lines[i + 1].TrimEnd('\r');
                        if (next.Trim().Length == 0) { block.Add(""); i++; continue; }
                        if (next.Length - next.TrimStart().Length <= indent) break;
                        block.Add(next.Trim());
                        i++;
                    }
                    value = string.Join(' ', block).Trim();
                }
                switch (key)
                {
                    case "cwd": result.Cwd = value; break;
                    case "branch": result.Branch = value; break;
                    case "repository": result.Repository = value; break;
                    case "summary": result.Summary = value; break;
                    case "name": result.Name = value; break;
                    case "user_named": result.UserNamed = value is "true" or "True" or "1"; break;
                    case "created_at" when Json.ParseTime(value) is > 0 and var t: result.CreatedAt = t; break;
                }
            }

            // /rename in the CLI wins; else summary, repo name, cwd folder.
            if (result.UserNamed == true && result.Name.Length > 0 && result.Name != dirName) result.Summary = result.Name;
            else if (result.Summary.Length > 0) result.Name = result.Summary;
            else if (result.Repository.Length > 0) result.Name = result.Repository.Split('/')[^1] is { Length: > 0 } r ? r : result.Repository;
            else if (result.Cwd.Length > 0)
            {
                var parts = result.Cwd.TrimEnd('/', '\\').Split('/', '\\');
                result.Name = parts[^1].Length > 0 ? parts[^1] : result.Cwd;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CopilotWorkspace { Name = dirName };
        }
    }

    private SessionSummary ToSummary(CopilotSession s) => WithWsl(new SessionSummary
    {
        Id = s.Id,
        Provider = "copilot",
        Status = s.Status,
        Cwd = s.Workspace.Cwd,
        Branch = s.Workspace.Branch,
        Repository = s.Workspace.Repository,
        Summary = s.Workspace.Summary,
        FirstPrompt = s.FirstPrompt,
        LatestPrompt = s.LatestPrompt,
        LatestPromptTime = s.LatestPromptTime,
        MessageCount = s.MessageCount,
        ToolCallCount = s.ToolCallCount,
        LastActivityTime = s.LastActivityTime,
        CreatedAt = s.Workspace.CreatedAt,
    });

    private SessionSummary WithWsl(SessionSummary s) => _wslDistro is null ? s : s with { Wsl = true, WslDistro = _wslDistro };

    public void Dispose() => StopWatching();

    [GeneratedRegex("^[\"']|[\"']$")] private static partial Regex Quotes();
    [GeneratedRegex(@"^[|>][-+]?\s*$")] private static partial Regex BlockIndicator();
}
