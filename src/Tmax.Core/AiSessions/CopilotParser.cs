using System.Collections.Concurrent;
using System.Text.Json;

namespace Tmax.Core.AiSessions;

/// <summary>
/// Incremental parser for Copilot CLI events.jsonl (port of src/main/copilot-events-parser.ts).
/// Keeps aggregates per file, not raw events, so memory stays bounded.
/// </summary>
public static class CopilotParser
{
    public sealed record Parsed(string Status, int MessageCount, int ToolCallCount, double LastActivityTime, int PendingToolCalls,
        long TotalTokens, string FirstPrompt, string LatestPrompt, double LatestPromptTime);

    private sealed class State
    {
        public long ByteOffset;
        public string Status = SessionStatus.Idle;
        public int MessageCount, ToolCallCount, PendingToolCalls;
        public double LastActivityTime, LatestPromptTime;
        public long TotalTokens;
        public string FirstPrompt = "", LatestPrompt = "";
        public List<string> RecentPrompts = [];

        public State Clone()
        {
            var c = (State)MemberwiseClone();
            c.RecentPrompts = [.. RecentPrompts];
            return c;
        }
    }

    private static readonly ConcurrentDictionary<string, State> Cache = new();
    private const int MaxCachedPrompts = 500;
    private const double ActiveThresholdMs = 30_000;

    public static Parsed? Parse(string eventsFile)
    {
        try
        {
            var info = new FileInfo(eventsFile);
            if (!info.Exists) return null;
            Cache.TryGetValue(eventsFile, out var cached);
            if (cached is not null && cached.ByteOffset >= info.Length) return ToResult(cached);
            if (cached is null && info.Length == 0) return null;

            var state = cached?.Clone() ?? new State();
            var (lines, offset) = Json.ReadNewLines(eventsFile, state.ByteOffset);
            foreach (var line in lines)
            {
                using var doc = Json.TryParse(line);
                if (doc is not null) Process(doc.RootElement, state);
            }
            state.ByteOffset = offset;
            Cache[eventsFile] = state;
            return ToResult(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string PromptText(JsonElement? data) =>
        (data?.Str("content") is { Length: > 0 } c ? c : data?.Str("transformedContent") ?? "").Trim();

    private static void Process(JsonElement raw, State s)
    {
        var type = raw.Str("type") ?? "unknown";
        var ts = raw.Str("timestamp") is { } t ? Json.ParseTime(t) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var data = raw.Obj("data");
        if (ts > s.LastActivityTime) s.LastActivityTime = ts;

        switch (type)
        {
            case "session.start":
            case "session.resume":
                s.Status = SessionStatus.Idle;
                s.PendingToolCalls = 0;
                break;
            case "assistant.turn_start":
                s.Status = SessionStatus.Thinking;
                break;
            case "assistant.turn_end":
                s.Status = SessionStatus.WaitingForUser;
                s.PendingToolCalls = 0;
                break;
            case "user.message":
            {
                s.MessageCount++;
                s.Status = SessionStatus.Thinking;
                var text = PromptText(data);
                if (text.Length > 0)
                {
                    if (s.FirstPrompt.Length == 0) s.FirstPrompt = Json.Clip(text, 300);
                    s.LatestPrompt = Json.Clip(text, 120).Replace('\n', ' ');
                    s.LatestPromptTime = ts;
                    s.RecentPrompts.Add(Json.Clip(text, 300));
                    if (s.RecentPrompts.Count > MaxCachedPrompts) s.RecentPrompts.RemoveAt(0);
                }
                break;
            }
            case "tool.execution_start":
                s.ToolCallCount++;
                s.PendingToolCalls++;
                s.Status = SessionStatus.ExecutingTool;
                break;
            case "tool.execution_complete":
                if (s.PendingToolCalls > 0) s.PendingToolCalls--;
                if (s.PendingToolCalls == 0) s.Status = SessionStatus.Thinking;
                break;
            case "confirmation_request":
            case "approval_request":
                s.Status = SessionStatus.AwaitingApproval;
                break;
            case "confirmation_response":
            case "approval_response":
                s.Status = SessionStatus.Thinking;
                break;
            case "input_request":
            case "user_input_request":
                s.Status = SessionStatus.WaitingForUser;
                break;
            case "token_usage":
                if (data is { } d)
                {
                    long tokens = 0;
                    foreach (var name in new[] { "total_tokens", "totalTokens" })
                        if (d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0) { tokens = n; break; }
                    if (tokens > 0) s.TotalTokens = tokens;
                }
                break;
        }
    }

    private static Parsed ToResult(State s)
    {
        // A busy status with no activity for 30 s is stale (the CLI was killed mid-turn).
        var stale = s.LastActivityTime > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.LastActivityTime > ActiveThresholdMs;
        var busy = s.Status is SessionStatus.ExecutingTool or SessionStatus.Thinking;
        return new Parsed(stale && busy ? SessionStatus.Idle : s.Status, s.MessageCount, s.ToolCallCount, s.LastActivityTime,
            s.PendingToolCalls, s.TotalTokens, s.FirstPrompt, s.LatestPrompt, s.LatestPromptTime);
    }

    public static List<string> ExtractPrompts(string eventsFile, int limit = 20)
    {
        if (Cache.TryGetValue(eventsFile, out var cached) && cached.RecentPrompts.Count > 0)
            return cached.RecentPrompts.TakeLast(limit).ToList();
        return PromptsWithTime(eventsFile, 300).Select(p => p.Text).TakeLast(limit).ToList();
    }

    public static List<PromptWithTime> PromptsWithTime(string eventsFile, int maxLen = 2000, int limit = 500)
    {
        try
        {
            var list = new List<PromptWithTime>();
            foreach (var line in Json.ReadAllLines(eventsFile))
            {
                using var doc = Json.TryParse(line);
                if (doc is null || doc.RootElement.Str("type") != "user.message") continue;
                var text = PromptText(doc.RootElement.Obj("data"));
                if (text.Length > 0) list.Add(new PromptWithTime(Json.Clip(text, maxLen), Json.ParseTime(doc.RootElement.Str("timestamp"))));
            }
            return list.TakeLast(limit).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>User prompts and assistant.message replies (tool-only turns have no text and are skipped).</summary>
    public static List<TranscriptEntry> Transcript(string eventsFile, int limit = 1000)
    {
        try
        {
            var list = new List<TranscriptEntry>();
            foreach (var line in Json.ReadAllLines(eventsFile))
            {
                using var doc = Json.TryParse(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                var time = Json.ParseTime(root.Str("timestamp"));
                switch (root.Str("type"))
                {
                    case "user.message" when PromptText(root.Obj("data")) is { Length: > 0 } text:
                        list.Add(new TranscriptEntry("user", Json.Clip(text, 4000), time));
                        break;
                    case "assistant.message" when (root.Obj("data")?.Str("content") ?? "").Trim() is { Length: > 0 } reply:
                        list.Add(new TranscriptEntry("assistant", Json.Clip(reply, 8000), time));
                        break;
                }
            }
            return list.TakeLast(limit).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void ClearCache(string eventsFile) => Cache.TryRemove(eventsFile, out _);
}
