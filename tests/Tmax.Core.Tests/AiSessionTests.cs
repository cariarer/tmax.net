using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tmax.Core.AiSessions;

namespace Tmax.Core.Tests;

public sealed class AiSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tmax-ai-").FullName;
    private static string Now(int offsetSeconds = 0) => DateTimeOffset.UtcNow.AddSeconds(offsetSeconds).ToString("O");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestDirs.Delete(_dir);
    }

    private string WriteJsonl(string path, params object[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines.Select(l => JsonSerializer.Serialize(l))) + "\n");
        return path;
    }

    private string Tmp(string name) => Path.Combine(_dir, name + ".jsonl");

    // ── Ported from tests/unit/main/task-176-ai-session-parser-status.test.ts (GH #118) ──

    [Fact]
    public void Copilot_turn_end_resets_pending_tools()
    {
        var f = WriteJsonl(Tmp("c1"), new { type = "session.start", timestamp = Now() }, new { type = "assistant.turn_start", timestamp = Now() },
            new { type = "tool.execution_start", timestamp = Now() }, new { type = "tool.execution_start", timestamp = Now() },
            new { type = "assistant.turn_end", timestamp = Now() });
        var r = CopilotParser.Parse(f)!;
        Assert.Equal(0, r.PendingToolCalls);
        Assert.NotEqual(SessionStatus.ExecutingTool, r.Status);
    }

    [Fact]
    public void Copilot_resume_zeros_pending_tools()
    {
        var f = WriteJsonl(Tmp("c2"), new { type = "tool.execution_start", timestamp = Now() }, new { type = "session.resume", timestamp = Now() });
        Assert.Equal(0, CopilotParser.Parse(f)!.PendingToolCalls);
    }

    [Fact]
    public void Copilot_stale_busy_session_is_idle()
    {
        var f = WriteJsonl(Tmp("c3"), new { type = "session.start", timestamp = Now(-60) }, new { type = "tool.execution_start", timestamp = Now(-60) });
        Assert.Equal(SessionStatus.Idle, CopilotParser.Parse(f)!.Status);
    }

    [Fact]
    public void Copilot_fresh_tool_call_is_executing()
    {
        var f = WriteJsonl(Tmp("c4"), new { type = "session.start", timestamp = Now() }, new { type = "assistant.turn_start", timestamp = Now() },
            new { type = "tool.execution_start", timestamp = Now() });
        var r = CopilotParser.Parse(f)!;
        Assert.Equal(SessionStatus.ExecutingTool, r.Status);
        Assert.Equal(1, r.PendingToolCalls);
    }

    [Fact]
    public void Claude_progress_after_end_turn_clears_waiting()
    {
        var f = WriteJsonl(Tmp("a1"), new { type = "user", timestamp = Now(), message = new { content = "hi" } },
            new { type = "assistant", timestamp = Now(), stop_reason = "end_turn" }, new { type = "progress", timestamp = Now() });
        Assert.NotEqual(SessionStatus.WaitingForUser, ClaudeCodeParser.Parse(f)!.Status);
    }

    [Fact]
    public void Claude_end_turn_alone_is_waiting()
    {
        var f = WriteJsonl(Tmp("a2"), new { type = "user", timestamp = Now(), message = new { content = "hi" } },
            new { type = "assistant", timestamp = Now(), stop_reason = "end_turn" });
        Assert.Equal(SessionStatus.WaitingForUser, ClaudeCodeParser.Parse(f)!.Status);
    }

    [Fact]
    public void Claude_assistant_after_end_turn_clears_waiting()
    {
        var f = WriteJsonl(Tmp("a3"), new { type = "user", timestamp = Now(), message = new { content = "hi" } },
            new { type = "assistant", timestamp = Now(), stop_reason = "end_turn" }, new { type = "assistant", timestamp = Now(), model = "claude" });
        Assert.NotEqual(SessionStatus.WaitingForUser, ClaudeCodeParser.Parse(f)!.Status);
    }

    // ── Claude Code ──

    private const string ClaudeId = "11111111-2222-3333-4444-555555555555";

    private string ClaudeSession(string root, params object[] lines) =>
        WriteJsonl(Path.Combine(root, "-proj", ClaudeId + ".jsonl"), lines);

    [Fact]
    public void Claude_parser_reads_meta_prompts_tools_and_appends_incrementally()
    {
        var f = ClaudeSession(Path.Combine(_dir, "claude"),
            new { type = "user", timestamp = Now(), sessionId = ClaudeId, cwd = "/home/u/repo", gitBranch = "main", slug = "calm-river",
                message = new { content = "<command-name>/review</command-name><command-args>pr 5</command-args>please" } },
            new { type = "assistant", timestamp = Now(), message = new { model = "claude-x", content = new object[] { new { type = "tool_use" }, new { type = "tool_use" } } } });

        var r = ClaudeCodeParser.Parse(f)!;
        Assert.Equal(ClaudeId, r.SessionId);
        Assert.Equal("/home/u/repo", r.Cwd);
        Assert.Equal("/review — please", r.FirstPrompt);
        Assert.Equal(2, r.ToolCallCount);
        Assert.Equal("claude-x", r.Model);

        // Partial line at the end is not consumed until it is complete.
        File.AppendAllText(f, JsonSerializer.Serialize(new { type = "user", timestamp = Now(), message = new { content = "second" } }) + "\n{\"type\":\"us");
        r = ClaudeCodeParser.Parse(f)!;
        Assert.Equal(2, r.MessageCount);
        Assert.Equal("second", r.LatestPrompt);
        File.AppendAllText(f, "er\",\"message\":{\"content\":\"third\"}}\n");
        Assert.Equal("third", ClaudeCodeParser.Parse(f)!.LatestPrompt);
    }

    [Fact]
    public void Claude_transcript_has_both_sides_and_skips_meta()
    {
        var f = ClaudeSession(Path.Combine(_dir, "claude2"),
            new { type = "user", timestamp = Now(), sessionId = ClaudeId, message = new { content = "Fix the bug" } },
            new { type = "user", timestamp = Now(), message = new { content = "<system-reminder>x</system-reminder>" } },
            new { type = "assistant", timestamp = Now(), message = new { content = new object[] { new { type = "text", text = "Done." } } } });
        var t = ClaudeCodeParser.Transcript(f);
        Assert.Equal([("user", "Fix the bug"), ("assistant", "Done.")], t.Select(e => (e.Role, e.Text)));
        Assert.Equal(["Fix the bug"], ClaudeCodeParser.ExtractPrompts(f));
    }

    [Fact]
    public async Task Claude_monitor_scans_and_reports_new_and_changed_sessions()
    {
        var root = Path.Combine(_dir, "claude3");
        ClaudeSession(root, new { type = "user", timestamp = Now(), sessionId = ClaudeId, cwd = "/x/proj", message = new { content = "first ask" } });
        using var monitor = new ClaudeCodeMonitor(Path.GetFullPath(root));
        var events = new ConcurrentQueue<string>();
        monitor.SessionAdded += s => events.Enqueue("added:" + s.Id);
        monitor.SessionUpdated += s => events.Enqueue($"updated:{s.Status}");

        var list = monitor.Scan();
        var s = Assert.Single(list);
        Assert.Equal("first ask", s.Summary);
        Assert.Equal("claude-code", s.Provider);
        Assert.Single(monitor.Search("FIRST AND proj"));
        Assert.Empty(monitor.Search("first NOT proj"));

        monitor.StartWatching();
        const string otherId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        WriteJsonl(Path.Combine(root, "-proj", otherId + ".jsonl"), new { type = "user", timestamp = Now(), sessionId = otherId, message = new { content = "new one" } });
        File.AppendAllText(Path.Combine(root, "-proj", ClaudeId + ".jsonl"),
            JsonSerializer.Serialize(new { type = "assistant", timestamp = Now(), stop_reason = "end_turn" }) + "\n");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((!events.Contains("added:" + otherId) || !events.Contains("updated:waitingForUser")) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Contains("added:" + otherId, events);
        Assert.Contains("updated:waitingForUser", events);
    }

    // ── Copilot ──

    [Fact]
    public void Copilot_workspace_yaml_and_user_named_sessions()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "ws", "sess-1")).FullName;
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: /home/u/app\nrepository: org/app\nbranch: dev\nsummary: >\n  Fix login\n  flow\ncreated_at: 2026-11-01T10:00:00Z\n");
        var ws = CopilotMonitor.ParseWorkspace(dir);
        Assert.Equal(("/home/u/app", "org/app", "dev", "Fix login flow", "Fix login flow"), (ws.Cwd, ws.Repository, ws.Branch, ws.Summary, ws.Name));
        Assert.NotNull(ws.CreatedAt);

        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "summary: auto\nname: My name\nuser_named: true\n");
        Assert.Equal("My name", CopilotMonitor.ParseWorkspace(dir).Summary);
    }

    [Fact]
    public void Copilot_monitor_filesystem_scan_and_traversal_safe_prompts()
    {
        var root = Path.Combine(_dir, "copilot", "session-state");
        WriteJsonl(Path.Combine(root, "s1", "events.jsonl"),
            new { type = "user.message", timestamp = Now(), data = new { content = "Add tests" } },
            new { type = "assistant.message", timestamp = Now(), data = new { content = "Added." } });
        using var monitor = new CopilotMonitor(root);

        var s = Assert.Single(monitor.Scan());
        Assert.Equal("Add tests", s.Summary);
        Assert.False(monitor.DbAvailable);
        Assert.Equal(["Add tests"], monitor.GetPrompts("s1"));
        Assert.Equal(2, monitor.GetTranscript("s1").Count);
        Assert.Empty(monitor.GetPrompts("../s1"));
    }

    [Fact]
    public void Copilot_db_queries_sessions_turns_fts_and_prompts()
    {
        var db = Path.Combine(_dir, "session-store.db");
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sessions (id TEXT, cwd TEXT, repository TEXT, branch TEXT, summary TEXT, updated_at TEXT, created_at TEXT);
                CREATE TABLE turns (session_id TEXT, user_message TEXT, timestamp TEXT);
                CREATE VIRTUAL TABLE search_index USING fts5(session_id, content);
                INSERT INTO sessions VALUES ('s1', '/w/app', 'org/app', 'main', 'Login refactor', datetime('now'), datetime('now','-1 day'));
                INSERT INTO sessions VALUES ('s2', '/w/old', '', 'x', 'Ancient', datetime('now','-90 days'), datetime('now','-90 days'));
                INSERT INTO turns VALUES ('s1', 'refactor the login form', datetime('now','-5 minutes'));
                INSERT INTO turns VALUES ('s1', 'now add tests please', datetime('now'));
                INSERT INTO search_index VALUES ('s1', 'login refactor form tests');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new CopilotSessionDb(db);
        Assert.True(store.TryOpen());
        var rows = store.QuerySessions()!;
        Assert.Equal("s1", Assert.Single(rows).Id); // older than 30 days filtered
        var stats = store.QueryTurnStats(["s1"])!["s1"];
        Assert.Equal(2, stats.MessageCount);
        Assert.Equal("now add tests please", stats.LatestPrompt);
        Assert.Equal("s1", Assert.Single(store.SearchSessions("logi")!).Id); // prefix match via FTS5
        Assert.Empty(store.SearchSessions("login AND nonexistent")!);
        Assert.Single(store.SearchPrompts("login")!);
        Assert.Single(store.SearchPrompts("tests NOT login")!);
        Assert.Equal(2, store.RecentPrompts()!.Count);
        Assert.Empty(store.SearchPrompts("' OR 1=1 --") ?? []); // parameterized, no injection
    }

    // ── Notifications & filters ──

    private static SessionSummary Session(string status, string id = "s", string provider = "claude-code") =>
        new() { Id = id, Provider = provider, Status = status, Summary = "Refactor auth", Branch = "main", LatestPrompt = "add tests" };

    [Fact]
    public void Notifier_fires_once_on_transition_and_honours_excludes_and_names()
    {
        var shown = new List<(string, string)>();
        var n = new SessionNotifier((t, b) => shown.Add((t, b)));

        Assert.Null(n.OnSessionUpdated(Session(SessionStatus.Thinking)));
        Assert.Equal(("Claude Code - Session Ready", "Refactor auth (main)\n\"add tests\""), n.OnSessionUpdated(Session(SessionStatus.WaitingForUser)));
        Assert.Null(n.OnSessionUpdated(Session(SessionStatus.WaitingForUser))); // no repeat while still waiting

        n.SetNameOverrides(new Dictionary<string, string> { ["c"] = "Pane name" });
        Assert.StartsWith("Pane name", n.OnSessionUpdated(Session(SessionStatus.AwaitingApproval, "c", "copilot"))!.Value.Body);

        n.SetExcludeStrings(["/^copilot/"]);
        Assert.Null(n.OnSessionUpdated(Session(SessionStatus.AwaitingApproval, "d", "copilot") with { Summary = "Other" }));
        n.Enabled = false;
        Assert.Null(n.OnSessionUpdated(Session(SessionStatus.WaitingForUser, "e")));
        Assert.Equal(2, shown.Count);
    }

    [Theory]
    [InlineData("fix the bug [Clawpilot context: Current date is X]", "fix the bug")]
    [InlineData("Here is the conversation:\nuser: old\nassistant: reply\nuser: new ask", "new ask")]
    public void ClawPilot_wrappers_are_stripped(string input, string expected) => Assert.Equal(expected, ClawPilot.StripContext(input));

    [Theory] // tests/unit/shared/and-filter.test.ts semantics
    [InlineData("foo AND bar", "foo bar baz", true)]
    [InlineData("foo AND qux", "foo bar", false)]
    [InlineData("foo NOT bar", "foo bar", false)]
    [InlineData("foo AND -bar", "foo only", true)]
    [InlineData("foo AND -bar", "foo bar", false)]
    [InlineData("", "anything", true)]
    public void And_filter(string query, string haystack, bool expected) =>
        Assert.Equal(expected, AndFilter.MatchesAll(haystack, AndFilter.Tokenize(query)));
}
