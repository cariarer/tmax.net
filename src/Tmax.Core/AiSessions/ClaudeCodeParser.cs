using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tmax.Core.AiSessions;

/// <summary>
/// Incremental parser for Claude Code session files (~/.claude/projects/*/&lt;uuid&gt;.jsonl),
/// port of src/main/claude-code-events-parser.ts. Only new complete lines are read on each call.
/// </summary>
public static partial class ClaudeCodeParser
{
    public sealed record Parsed(string SessionId, string Slug, string Cwd, string GitBranch, string Model, string Status,
        int MessageCount, int ToolCallCount, double LastActivityTime, string FirstPrompt, string LatestPrompt, double LatestPromptTime);

    private sealed class State
    {
        public long ByteOffset;
        public bool MetaExtracted;
        public string SessionId = "", Slug = "", Cwd = "", GitBranch = "", Model = "";
        public int MessageCount, ToolCallCount;
        public double LastActivityTime, LatestPromptTime;
        public string FirstPrompt = "", LatestPrompt = "", LastLineType = "";
        public bool LastLineHasEndTurn, AwaitingInput;
        public State Clone() => (State)MemberwiseClone();
    }

    private static readonly ConcurrentDictionary<string, State> Cache = new();
    private const double ActiveThresholdMs = 30_000;
    private const double WaitingThresholdMs = 10 * 60_000;

    public static Parsed? Parse(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0) return null;
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

            Cache.TryGetValue(filePath, out var cached);
            if (cached is not null && cached.ByteOffset >= info.Length) return Derive(cached, mtime);

            var state = cached?.Clone() ?? new State();
            var (lines, newOffset) = Json.ReadNewLines(filePath, state.ByteOffset);
            foreach (var line in lines) ProcessLine(line, state);
            state.ByteOffset = newOffset;
            Cache[filePath] = state;
            return Derive(state, mtime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ProcessLine(string line, State state)
    {
        var type = FirstType().Match(line) is { Success: true } m1 ? m1.Groups[1].Value
            : AnyType().Match(line) is { Success: true } m2 ? m2.Groups[1].Value : null;
        if (type is null) return;

        state.LastLineType = type;
        state.LastLineHasEndTurn = false;
        // GH #118: any line clears "awaiting input"; a real end_turn sets it again below.
        state.AwaitingInput = false;

        double lineTs = 0;
        if (Timestamp().Match(line) is { Success: true } ts)
        {
            lineTs = Json.ParseTime(ts.Groups[1].Value);
            if (lineTs > state.LastActivityTime) state.LastActivityTime = lineTs;
        }

        switch (type)
        {
            case "user":
            {
                state.MessageCount++;
                string? userText = null;
                using (var doc = Json.TryParse(line))
                {
                    if (doc is not null)
                    {
                        var root = doc.RootElement;
                        if (!state.MetaExtracted)
                        {
                            state.Cwd = root.Str("cwd") ?? "";
                            state.GitBranch = root.Str("gitBranch") ?? "";
                            state.Slug = root.Str("slug") ?? "";
                            state.SessionId = root.Str("sessionId") ?? "";
                            state.MetaExtracted = true;
                        }
                        else if (root.Str("gitBranch") is { Length: > 0 } branch)
                        {
                            state.GitBranch = branch; // branch may change during a session
                        }
                        if (root.Obj("message") is { } msg && msg.Obj("content") is { } content)
                            userText = ExtractText(content);
                    }
                    else if (!state.MetaExtracted)
                    {
                        // Unparseable line: best-effort metadata via regex.
                        if (CwdField().Match(line) is { Success: true } c)
                        {
                            try { state.Cwd = JsonSerializer.Deserialize<string>($"\"{c.Groups[1].Value}\"") ?? ""; }
                            catch (JsonException) { state.Cwd = c.Groups[1].Value; }
                        }
                        if (SlugField().Match(line) is { Success: true } s) state.Slug = s.Groups[1].Value;
                        if (BranchField().Match(line) is { Success: true } b) state.GitBranch = b.Groups[1].Value;
                        if (SessionField().Match(line) is { Success: true } id) state.SessionId = id.Groups[1].Value;
                        state.MetaExtracted = true;
                    }
                }
                if (userText is not null && !userText.StartsWith("[Request interrupted", StringComparison.Ordinal))
                {
                    var summary = FormatFirstPromptSummary(userText);
                    if (summary.Length > 0)
                    {
                        if (state.FirstPrompt.Length == 0) state.FirstPrompt = summary;
                        state.LatestPrompt = summary;
                        if (lineTs > 0) state.LatestPromptTime = lineTs;
                    }
                }
                break;
            }
            case "assistant":
                if (ModelField().Match(line) is { Success: true } model) state.Model = model.Groups[1].Value;
                state.ToolCallCount += ToolUse().Count(line);
                if (line.Contains("\"end_turn\"", StringComparison.Ordinal))
                {
                    state.LastLineHasEndTurn = true;
                    state.AwaitingInput = true;
                }
                break;
        }
    }

    private static Parsed Derive(State s, double mtime)
    {
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - mtime;
        var isRecent = age < ActiveThresholdMs;
        var isRecentWaiting = age < WaitingThresholdMs;
        var status = SessionStatus.Idle;
        if (s.AwaitingInput && isRecentWaiting) status = SessionStatus.WaitingForUser;
        else if (isRecent)
        {
            status = s.LastLineType switch
            {
                "progress" => s.AwaitingInput ? SessionStatus.WaitingForUser : SessionStatus.ExecutingTool,
                "user" => SessionStatus.Thinking,
                "assistant" => s.LastLineHasEndTurn ? SessionStatus.WaitingForUser : SessionStatus.Thinking,
                "system" => SessionStatus.WaitingForUser,
                _ => SessionStatus.Thinking,
            };
        }
        return new Parsed(s.SessionId, s.Slug, s.Cwd, s.GitBranch, s.Model, status, s.MessageCount, s.ToolCallCount,
            s.LastActivityTime, s.FirstPrompt, s.LatestPrompt, s.LatestPromptTime);
    }

    /// <summary>First text of a message content (string or array of blocks).</summary>
    private static string? ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString()!.Trim() is { Length: > 0 } t ? t : null;
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.Str("type") == "text" && block.Str("text") is { Length: > 0 } text)
                return text.Trim() is { Length: > 0 } t ? t : null;
        }
        return null;
    }

    private static string ExtractAllText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!.Trim();
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join('\n', content.EnumerateArray()
            .Where(b => b.Str("type") == "text" && b.Str("text") is { Length: > 0 })
            .Select(b => b.Str("text"))).Trim();
    }

    /// <summary>Claude Code's agent metadata wrappers (command-message, system-reminder, …): any balanced tag pair.</summary>
    public static string StripCommandXml(string s) => XmlPair().Replace(s, "");

    /// <summary>Slash commands become "/name — rest"; meta-only messages become "".</summary>
    public static string FormatFirstPromptSummary(string raw)
    {
        string output;
        if (CommandName().Match(raw) is { Success: true } name)
        {
            var cmd = name.Groups[1].Value.TrimStart('/');
            var rest = StripCommandXml(raw).TrimStart('/').Trim();
            output = rest.Length > 0 ? $"/{cmd} — {rest}" : $"/{cmd}";
        }
        else
        {
            var stripped = StripCommandXml(raw).Trim();
            if (stripped.Length == 0) return "";
            output = stripped;
        }
        output = Ansi().Replace(output, "");
        return Json.Clip(output, 120).Replace('\n', ' ').Trim();
    }

    private static string? DisplayPrompt(string text)
    {
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal)) return null;
        var stripped = StripCommandXml(text).Trim();
        if (stripped.Length == 0) return null;
        return CommandName().Match(text) is { Success: true } m
            ? $"/{m.Groups[1].Value.TrimStart('/')} — {stripped}"
            : stripped;
    }

    private static IEnumerable<(JsonElement Root, double Time)> Entries(string filePath)
    {
        foreach (var line in Json.ReadAllLines(filePath))
        {
            using var doc = Json.TryParse(line);
            if (doc is null) continue;
            yield return (doc.RootElement.Clone(), Json.ParseTime(doc.RootElement.Str("timestamp")));
        }
    }

    public static List<string> ExtractPrompts(string filePath, int limit = 10) =>
        PromptsWithTime(filePath, 300).Select(p => p.Text).TakeLast(limit).ToList();

    public static List<PromptWithTime> PromptsWithTime(string filePath, int maxLen = 2000, int limit = 500)
    {
        try
        {
            var list = new List<PromptWithTime>();
            foreach (var (root, time) in Entries(filePath))
            {
                if (root.Str("type") != "user" || root.Obj("message")?.Obj("content") is not { } content) continue;
                if (ExtractText(content) is not { } text || DisplayPrompt(text) is not { } display) continue;
                list.Add(new PromptWithTime(Json.Clip(display, maxLen).Replace('\n', ' '), time));
            }
            return list.TakeLast(limit).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Two-sided chat (Claude Code stores assistant text too), for the timeline panel.</summary>
    public static List<TranscriptEntry> Transcript(string filePath, int limit = 1000)
    {
        try
        {
            var list = new List<TranscriptEntry>();
            foreach (var (root, time) in Entries(filePath))
            {
                if (root.Obj("message")?.Obj("content") is not { } content) continue;
                var type = root.Str("type");
                if (type == "user" && ExtractText(content) is { } text && DisplayPrompt(text) is { } display)
                    list.Add(new TranscriptEntry("user", display, time));
                else if (type == "assistant" && ExtractAllText(content) is { Length: > 0 } reply)
                    list.Add(new TranscriptEntry("assistant", reply, time));
            }
            return list.TakeLast(limit).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void ClearCache(string filePath) => Cache.TryRemove(filePath, out _);

    [GeneratedRegex("^\\s*\\{\\s*\"type\"\\s*:\\s*\"([^\"]+)\"")] private static partial Regex FirstType();
    [GeneratedRegex("\"type\"\\s*:\\s*\"([^\"]+)\"")] private static partial Regex AnyType();
    [GeneratedRegex("\"timestamp\"\\s*:\\s*\"([^\"]+)\"")] private static partial Regex Timestamp();
    [GeneratedRegex("\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")] private static partial Regex CwdField();
    [GeneratedRegex("\"slug\"\\s*:\\s*\"([^\"]+)\"")] private static partial Regex SlugField();
    [GeneratedRegex("\"gitBranch\"\\s*:\\s*\"([^\"]+)\"")] private static partial Regex BranchField();
    [GeneratedRegex("\"sessionId\"\\s*:\\s*\"([^\"]+)\"")] private static partial Regex SessionField();
    [GeneratedRegex("\"model\"\\s*:\\s*\"([^\"]+)\"")] private static partial Regex ModelField();
    [GeneratedRegex("\"type\"\\s*:\\s*\"tool_use\"")] private static partial Regex ToolUse();
    [GeneratedRegex("<command-name>([^<]+)</command-name>")] private static partial Regex CommandName();
    [GeneratedRegex(@"<([a-zA-Z][a-zA-Z0-9-]*)\b[^>]*>[\s\S]*?</\1>")] private static partial Regex XmlPair();
    [GeneratedRegex("\\x1b?\\[[0-9;]*[A-Za-z]")] private static partial Regex Ansi();
}
