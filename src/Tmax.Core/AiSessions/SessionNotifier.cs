using System.Text.RegularExpressions;

namespace Tmax.Core.AiSessions;

/// <summary>
/// Decides when an AI session deserves an OS notification and what it says (port of
/// src/main/copilot-notification.ts): only on the transition into "needs attention", with a 5 s
/// per-session flicker cooldown, an 8 s duplicate-body filter, the user's exclude list and the
/// user's pane names. Showing it is the host's job (<see cref="Show"/>).
/// </summary>
public sealed partial class SessionNotifier(Action<string, string> show)
{
    private const int MaxTracked = 500;
    private static readonly TimeSpan FlickerCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(8);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTime> _lastNotified = [];
    private readonly Dictionary<string, string> _lastStatus = [];
    private readonly List<(string Key, DateTime Time)> _recentBodies = [];
    private string[] _exclude = [];
    private Dictionary<string, string> _nameOverrides = [];

    public Action<string, string> Show { get; } = show;
    public bool Enabled { get; set; } = true;

    public void SetExcludeStrings(IEnumerable<string>? values) { lock (_lock) _exclude = values?.ToArray() ?? []; }

    public void SetNameOverrides(IReadOnlyDictionary<string, string>? map)
    {
        lock (_lock)
            _nameOverrides = map?.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim()) ?? [];
    }

    public void Forget(string sessionId)
    {
        lock (_lock) { _lastNotified.Remove(sessionId); _lastStatus.Remove(sessionId); }
    }

    /// <returns>The (title, body) shown, or null.</returns>
    public (string Title, string Body)? OnSessionUpdated(SessionSummary s)
    {
        string title, body;
        lock (_lock)
        {
            while (_lastStatus.Count > MaxTracked) _lastStatus.Remove(_lastStatus.Keys.First());
            while (_lastNotified.Count > MaxTracked) _lastNotified.Remove(_lastNotified.Keys.First());

            var prev = _lastStatus.GetValueOrDefault(s.Id);
            _lastStatus[s.Id] = s.Status;
            if (!Enabled || !SessionStatus.NeedsAttention(s.Status) || (prev is not null && SessionStatus.NeedsAttention(prev))) return null;

            var now = DateTime.UtcNow;
            if (_lastNotified.TryGetValue(s.Id, out var last) && now - last < FlickerCooldown) return null;
            _lastNotified[s.Id] = now;

            var isClaude = s.Provider == "claude-code";
            var state = s.Status == SessionStatus.AwaitingApproval ? "Approval Needed" : isClaude ? "Session Ready" : "Waiting for Input";
            title = $"{(isClaude ? "Claude Code" : "Copilot")} - {state}";
            body = BuildBody(s);
            if (IsExcluded(title, body)) return null;

            _recentBodies.RemoveAll(e => now - e.Time > DedupWindow);
            var key = DedupKey(body);
            if (body.Length > 0 && _recentBodies.Any(e => e.Key == key)) return null;
            if (body.Length > 0) _recentBodies.Add((key, now));
        }
        Show(title, body);
        return (title, body);
    }

    private static string DedupKey(string body) =>
        Spaces().Replace(Json.Clip(body.Trim(), 160).ToLowerInvariant(), " ");

    private bool IsExcluded(string title, string body)
    {
        var raw = $"{title}\n{body}";
        var lower = raw.ToLowerInvariant();
        foreach (var entry in _exclude)
        {
            var t = entry?.Trim() ?? "";
            if (t.Length == 0) continue;
            if (t.Length >= 3 && t[0] == '/' && t[^1] == '/')
            {
                try
                {
                    if (Regex.IsMatch(raw, t[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) return true;
                }
                catch (ArgumentException) { /* invalid pattern: ignore entry */ }
                catch (RegexMatchTimeoutException) { }
                continue;
            }
            if (lower.Contains(t.ToLowerInvariant(), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private string BuildBody(SessionSummary s)
    {
        var folder = "";
        if (!string.IsNullOrEmpty(s.Cwd))
        {
            var parts = s.Cwd.TrimEnd('/', '\\').Split('/', '\\');
            folder = parts[^1].Length > 0 ? parts[^1] : s.Cwd;
        }
        var summary = s.Summary.Length > 0 && s.Summary != s.Slug ? ClawPilot.StripContext(Spaces().Replace(s.Summary.Trim(), " ")) : "";
        var name = _nameOverrides.GetValueOrDefault(s.Id) is { Length: > 0 } o ? o
            : summary.Length > 0 ? summary : s.Repository.Length > 0 ? s.Repository : folder.Length > 0 ? folder : Json.Clip(s.Id, 8);
        var display = name.Length > 60 ? name[..59] + "…" : name;
        var lines = new List<string> { s.Branch.Length > 0 ? $"{display} ({s.Branch})" : display };
        var prompt = ClawPilot.StripContext(Spaces().Replace((s.LatestPrompt ?? "").Trim(), " "));
        if (prompt.Length > 0 && prompt != name) lines.Add($"\"{(prompt.Length > 80 ? prompt[..79] + "…" : prompt)}\"");
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}

/// <summary>ClawPilot prompt wrappers (port of stripClawpilotContext in src/shared/copilot-types.ts).</summary>
public static partial class ClawPilot
{
    public static string StripContext(string s)
    {
        var output = ContextSuffix().Replace(s, "");
        var header = ConversationHeader().Match(output);
        if (header.Success)
        {
            var after = output[(header.Index + header.Length)..];
            var users = UserTurn().Matches(after);
            if (users.Count == 0) output = output[..header.Index];
            else
            {
                var last = users[^1];
                var tail = after[(last.Index + last.Length)..];
                var stop = AssistantTurn().Match(tail);
                var prompt = stop.Success ? tail[..stop.Index] : tail;
                output = (output[..header.Index] + prompt).Trim();
            }
        }
        return output.Trim();
    }

    [GeneratedRegex(@"\s*\[clawpilot context:[\s\S]*$", RegexOptions.IgnoreCase)] private static partial Regex ContextSuffix();
    [GeneratedRegex(@"here is the conversation:\s*", RegexOptions.IgnoreCase)] private static partial Regex ConversationHeader();
    [GeneratedRegex(@"(?:^|\n)\s*user:\s*", RegexOptions.IgnoreCase)] private static partial Regex UserTurn();
    [GeneratedRegex(@"\n\s*assistant:\s*", RegexOptions.IgnoreCase)] private static partial Regex AssistantTurn();
}
