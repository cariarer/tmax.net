using System.Text.RegularExpressions;

namespace Tmax.Core.AiSessions;

/// <summary>Claude Code session list (port of src/main/claude-code-session-monitor.ts + its watcher wiring).</summary>
public sealed partial class ClaudeCodeMonitor : ISessionMonitorEvents, IDisposable
{
    private const int MaxCandidates = 1000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SessionSummary> _sessions = [];
    private readonly Dictionary<string, string> _filePaths = []; // sessionId -> file
    private readonly string? _wslDistro;
    private List<(string File, double Mtime)>? _candidates;
    private SessionFileWatcher? _watcher;

    public ClaudeCodeMonitor(string? basePath = null, string? wslDistro = null)
    {
        BasePath = basePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        _wslDistro = wslDistro;
    }

    public string BasePath { get; }
    public int LastTotalEligible { get; private set; }

    public event Action<SessionSummary>? SessionAdded;
    public event Action<SessionSummary>? SessionUpdated;
    public event Action<string>? SessionRemoved;

    public static bool IsSessionFile(string name) => SessionFileName().IsMatch(name);

    // ── Full scan ────────────────────────────────────────────────────

    public List<SessionSummary> Scan(int limit = 314)
    {
        var added = new List<SessionSummary>();
        List<SessionSummary> result;
        lock (_lock)
        {
            _candidates ??= FindCandidates();
            LastTotalEligible = _candidates.Count;
            result = [];
            var current = new HashSet<string>();
            foreach (var (file, _) in _candidates.Take(limit).ToList())
            {
                var existing = _filePaths.FirstOrDefault(kv => kv.Value == file).Key;
                if (existing is not null && _sessions.TryGetValue(existing, out var known))
                {
                    current.Add(existing);
                    result.Add(known);
                    continue;
                }
                var summary = Load(file);
                if (summary is null)
                {
                    _candidates.RemoveAll(c => c.File == file);
                    LastTotalEligible = _candidates.Count;
                    continue;
                }
                current.Add(summary.Id);
                _sessions[summary.Id] = summary;
                _filePaths[summary.Id] = file;
                result.Add(summary);
                added.Add(summary);
            }
            // Evict sessions outside the top N silently (they still exist on disk).
            foreach (var (id, fp) in _filePaths.Where(kv => !current.Contains(kv.Key)).ToList())
            {
                _sessions.Remove(id);
                _filePaths.Remove(id);
                ClaudeCodeParser.ClearCache(fp);
            }
        }
        foreach (var s in added) SessionAdded?.Invoke(s);
        return result;
    }

    private List<(string, double)> FindCandidates()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        var list = new List<(string, double)>();
        if (!Directory.Exists(BasePath)) return list;
        foreach (var dir in SafeEnumerate(() => Directory.EnumerateDirectories(BasePath)))
        {
            foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
            {
                if (!IsSessionFile(Path.GetFileName(file))) continue;
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
                if (mtime < cutoff) continue;
                list.Add((file, mtime.ToUnixTimeMilliseconds()));
            }
        }
        return list.OrderByDescending(c => c.Item2).Take(MaxCandidates).ToList();
    }

    private static List<string> SafeEnumerate(Func<IEnumerable<string>> f)
    {
        try { return f().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public void InvalidateCache()
    {
        lock (_lock) _candidates = null;
    }

    // ── Accessors ────────────────────────────────────────────────────

    public SessionSummary? Get(string id)
    {
        lock (_lock) return _sessions.GetValueOrDefault(id);
    }

    /// <summary>Searches in-memory metadata only (TASK-131), with AND/NOT syntax.</summary>
    public List<SessionSummary> Search(string query)
    {
        var tokens = AndFilter.Tokenize(query);
        if (tokens.Count == 0) return [];
        lock (_lock)
        {
            return _sessions.Values.Where(s => AndFilter.MatchesAll(
                string.Join('\n', s.Summary, s.Branch, s.Cwd, s.LatestPrompt ?? "", s.Id).ToLowerInvariant(), tokens)).ToList();
        }
    }

    private string? FileOf(string id)
    {
        lock (_lock) return _filePaths.GetValueOrDefault(id);
    }

    public List<string> GetPrompts(string id, int limit = 10) =>
        FileOf(id) is { } f ? ClaudeCodeParser.ExtractPrompts(f, limit) : [];

    public List<TranscriptEntry> GetTranscript(string id) =>
        FileOf(id) is { } f ? ClaudeCodeParser.Transcript(f) : [];

    // ── Watcher ──────────────────────────────────────────────────────

    public void StartWatching()
    {
        lock (_lock)
        {
            if (_watcher is not null) return;
            _watcher = new SessionFileWatcher(BasePath, IsSessionFile);
        }
        _watcher.Added += OnNewFile;
        _watcher.Changed += OnFileChanged;
        _watcher.Removed += OnFileRemoved;
        _watcher.StaleCheck += RefreshLoaded;
        _watcher.Start();
    }

    public void StopWatching()
    {
        SessionFileWatcher? w;
        lock (_lock) { w = _watcher; _watcher = null; }
        w?.Dispose();
    }

    public void OnFileChanged(string file)
    {
        lock (_lock)
        {
            var idx = _candidates?.FindIndex(c => c.File == file) ?? -1;
            if (idx > 0)
            {
                _candidates!.RemoveAt(idx);
                _candidates.Insert(0, (file, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
        }
        Refresh(file);
    }

    public void OnNewFile(string file)
    {
        SessionSummary? summary;
        lock (_lock)
        {
            if (_candidates is not null && _candidates.All(c => c.File != file))
            {
                _candidates.Insert(0, (file, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                if (_candidates.Count > MaxCandidates) _candidates.RemoveAt(_candidates.Count - 1);
                LastTotalEligible = _candidates.Count;
            }
            summary = Load(file);
            if (summary is not null)
            {
                _sessions[summary.Id] = summary;
                _filePaths[summary.Id] = file;
            }
        }
        if (summary is not null) SessionAdded?.Invoke(summary);
    }

    public void OnFileRemoved(string file)
    {
        string? removed;
        lock (_lock)
        {
            removed = _filePaths.FirstOrDefault(kv => kv.Value == file).Key;
            if (removed is not null)
            {
                _sessions.Remove(removed);
                _filePaths.Remove(removed);
                ClaudeCodeParser.ClearCache(file);
            }
            if (_candidates is not null)
            {
                _candidates.RemoveAll(c => c.File == file);
                LastTotalEligible = _candidates.Count;
            }
        }
        if (removed is not null) SessionRemoved?.Invoke(removed);
    }

    private SessionSummary? Refresh(string file)
    {
        if (!File.Exists(file))
        {
            OnFileRemoved(file);
            return null;
        }
        SessionSummary? summary, old;
        lock (_lock)
        {
            summary = Load(file);
            if (summary is null) return null;
            old = _sessions.GetValueOrDefault(summary.Id);
            _sessions[summary.Id] = summary;
            _filePaths[summary.Id] = file;
        }
        if (old is not null && (old.Status != summary.Status || old.MessageCount != summary.MessageCount
                                || old.ToolCallCount != summary.ToolCallCount || old.Summary != summary.Summary
                                || old.LatestPrompt != summary.LatestPrompt))
            SessionUpdated?.Invoke(summary);
        return summary;
    }

    /// <summary>Re-check sessions that are not idle (their status decays with time, not with file writes).</summary>
    public void RefreshLoaded()
    {
        List<string> files;
        lock (_lock) files = _sessions.Values.Where(s => s.Status != SessionStatus.Idle).Select(s => _filePaths[s.Id]).ToList();
        foreach (var f in files) Refresh(f);
    }

    private SessionSummary? Load(string file)
    {
        var p = ClaudeCodeParser.Parse(file);
        if (p is null || p.SessionId.Length == 0) return null;
        var folder = "";
        if (p.Cwd.Length > 0)
        {
            var parts = p.Cwd.TrimEnd('/', '\\').Split('/', '\\');
            folder = parts[^1].Length > 0 ? parts[^1] : p.Cwd;
        }
        return new SessionSummary
        {
            Id = p.SessionId,
            Provider = "claude-code",
            Status = p.Status,
            Cwd = p.Cwd,
            Branch = p.GitBranch,
            Summary = p.FirstPrompt.Length > 0 ? p.FirstPrompt : p.Slug.Length > 0 ? p.Slug : folder,
            Slug = NullIfEmpty(p.Slug),
            FirstPrompt = NullIfEmpty(p.FirstPrompt),
            LatestPrompt = NullIfEmpty(p.LatestPrompt),
            LatestPromptTime = p.LatestPromptTime > 0 ? p.LatestPromptTime : null,
            MessageCount = p.MessageCount,
            ToolCallCount = p.ToolCallCount,
            LastActivityTime = p.LastActivityTime,
            Model = NullIfEmpty(p.Model),
            Wsl = _wslDistro is null ? null : true,
            WslDistro = _wslDistro,
        };
    }

    private static string? NullIfEmpty(string s) => s.Length > 0 ? s : null;

    public void Dispose() => StopWatching();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.jsonl$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionFileName();
}
