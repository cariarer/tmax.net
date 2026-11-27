using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Tmax.Core.AiSessions;

/// <summary>
/// Read-only access to the Copilot CLI's ~/.copilot/session-store.db (port of src/main/copilot-session-db.ts).
/// Microsoft.Data.Sqlite ships a prebuilt SQLite (with FTS5) for every platform, so nothing is compiled.
/// Each call opens a pooled read-only connection, so queries from different threads don't block each other
/// (the original needed a worker thread for that).
/// </summary>
public sealed partial class CopilotSessionDb(string? dbPath = null)
{
    public string DbPath { get; } = dbPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-store.db");

    public sealed record SessionRow(string Id, string Cwd, string Repository, string Branch, string Summary, string UpdatedAt, string CreatedAt);

    public sealed record TurnStats(string SessionId, int MessageCount, string? LatestPrompt, string? LatestPromptTime);

    /// <summary>Row shape of copilot:searchPrompts / copilot:getRecentPrompts (snake_case like the original).</summary>
    public sealed record PromptRow(string session_id, string user_message, string timestamp, string summary, string cwd);

    public bool Exists => File.Exists(DbPath);

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        return c;
    }

    /// <summary>True when the DB exists and has the expected tables.</summary>
    public bool TryOpen()
    {
        if (!Exists) return false;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type IN ('table','view') AND name IN ('sessions','turns')";
            return Convert.ToInt32(cmd.ExecuteScalar()) == 2;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";

    private List<T>? Query<T>(string sql, Func<SqliteDataReader, T> map, params object[] args)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", args[i]);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static SessionRow MapSession(SqliteDataReader r) => new(S(r, 0), S(r, 1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), S(r, 6));
    private static PromptRow MapPrompt(SqliteDataReader r) => new(S(r, 0), S(r, 1), S(r, 2), S(r, 3), S(r, 4));

    public List<SessionRow>? QuerySessions(int limit = 314) => Query(
        "SELECT id, cwd, repository, branch, summary, updated_at, created_at FROM sessions " +
        "WHERE updated_at > datetime('now', '-30 days') ORDER BY updated_at DESC LIMIT $p0", MapSession, limit);

    public int? TotalEligibleCount() =>
        Query("SELECT COUNT(*) FROM sessions WHERE updated_at > datetime('now', '-30 days')", r => r.GetInt32(0))?.FirstOrDefault();

    public Dictionary<string, TurnStats>? QueryTurnStats(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return [];
        var placeholders = string.Join(',', ids.Select((_, i) => $"$p{i}"));
        var rows = Query(
            "SELECT session_id, COUNT(*) AS message_count, " +
            "(SELECT user_message FROM turns t2 WHERE t2.session_id = t1.session_id ORDER BY timestamp DESC LIMIT 1) AS latest_prompt, " +
            $"MAX(timestamp) AS latest_prompt_time FROM turns t1 WHERE session_id IN ({placeholders}) GROUP BY session_id",
            r => new TurnStats(S(r, 0), r.GetInt32(1), r.IsDBNull(2) ? null : S(r, 2), r.IsDBNull(3) ? null : S(r, 3)),
            ids.Cast<object>().ToArray());
        return rows?.ToDictionary(r => r.SessionId);
    }

    /// <summary>
    /// Full-text search via the CLI's search_index (FTS5). Words become prefix terms; AND/OR/NOT pass
    /// through. Plain AND queries are post-filtered on metadata so every token must really appear.
    /// </summary>
    public List<SessionRow>? SearchSessions(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        static string Prefix(string raw)
        {
            var cleaned = NonWord().Replace(raw, " ").Trim();
            return cleaned.Length == 0 ? "" : string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w + "*"));
        }

        string fts;
        if (Operators().IsMatch(query))
        {
            var parts = OperatorsCi().Split(query)
                .Select(p => OperatorWord().IsMatch(p.Trim()) ? p.Trim().ToUpperInvariant() : Prefix(p))
                .Where(p => p.Length > 0).ToList();
            while (parts.Count > 0 && OperatorWord().IsMatch(parts[^1])) parts.RemoveAt(parts.Count - 1);
            while (parts.Count > 0 && OperatorWord().IsMatch(parts[0])) parts.RemoveAt(0);
            fts = string.Join(' ', parts);
        }
        else fts = Prefix(query);
        if (fts.Trim().Length == 0) return null;

        var skipMetadataFilter = OrNot().IsMatch(query);
        var rows = Query(
            "SELECT DISTINCT s.id, s.cwd, s.repository, s.branch, s.summary, s.updated_at, s.created_at " +
            "FROM search_index si JOIN sessions s ON s.id = si.session_id WHERE search_index MATCH $p0 " +
            "ORDER BY s.updated_at DESC LIMIT $p1", MapSession, fts, limit * (skipMetadataFilter ? 1 : 4));
        if (rows is null || skipMetadataFilter) return rows;

        var tokens = AndWord().Split(query)
            .Select(t => NonWordKeepSpace().Replace(t, " ").Trim().ToLowerInvariant())
            .Where(t => t.Length > 0).SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (tokens.Count == 0) return rows;
        return rows.Where(r =>
        {
            var hay = string.Join('\n', new[] { r.Summary, r.Cwd, r.Repository, r.Branch }.Where(x => x.Length > 0)).ToLowerInvariant();
            return tokens.All(hay.Contains);
        }).Take(limit).ToList();
    }

    private const string PromptSelect =
        "SELECT t.session_id, t.user_message, t.timestamp, s.summary, s.cwd FROM turns t JOIN sessions s ON s.id = t.session_id ";

    /// <summary>Prompt search with LIKE (AND/OR/NOT between terms), newest first.</summary>
    public List<PromptRow>? SearchPrompts(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (!OperatorsCi().IsMatch(query))
            return Query(PromptSelect + "WHERE t.user_message LIKE $p0 AND t.user_message IS NOT NULL AND length(t.user_message) > 3 " +
                         "ORDER BY t.timestamp DESC LIMIT $p1", MapPrompt, $"%{query}%", limit);

        var conditions = new List<string>();
        var args = new List<object>();
        var op = "AND";
        var negate = false;
        foreach (var part in OperatorsCi().Split(query).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var kw = part.ToUpperInvariant();
            if (kw is "AND" or "OR") op = kw;
            else if (kw == "NOT") negate = true;
            else
            {
                if (conditions.Count > 0) conditions.Add(op);
                conditions.Add($"t.user_message {(negate ? "NOT LIKE" : "LIKE")} $p{args.Count}");
                args.Add($"%{part}%");
                negate = false;
            }
        }
        if (conditions.Count == 0) return null;
        args.Add(limit);
        return Query(PromptSelect + $"WHERE ({string.Join(' ', conditions)}) AND t.user_message IS NOT NULL AND length(t.user_message) > 3 " +
                     $"ORDER BY t.timestamp DESC LIMIT $p{args.Count - 1}", MapPrompt, args.ToArray());
    }

    public List<PromptRow>? RecentPrompts(int limit = 300) => Query(
        PromptSelect + "WHERE t.user_message IS NOT NULL AND length(t.user_message) > 3 ORDER BY t.timestamp DESC LIMIT $p0",
        MapPrompt, limit);

    public static SessionSummary ToSummary(SessionRow row, TurnStats? stats) => new()
    {
        Id = row.Id,
        Provider = "copilot",
        Cwd = row.Cwd,
        Branch = row.Branch,
        Repository = row.Repository,
        Summary = row.Summary,
        MessageCount = stats?.MessageCount ?? 0,
        LastActivityTime = Json.ParseTime(row.UpdatedAt),
        LatestPrompt = stats?.LatestPrompt is { } p ? Json.Clip(p, 120) : null,
        LatestPromptTime = stats?.LatestPromptTime is { } t && Json.ParseTime(t) is > 0 and var ms ? ms : null,
        CreatedAt = Json.ParseTime(row.CreatedAt) is > 0 and var c ? c : null,
    };

    [GeneratedRegex(@"\b(AND|OR|NOT)\b")] private static partial Regex Operators();
    [GeneratedRegex(@"\b(AND|OR|NOT)\b", RegexOptions.IgnoreCase)] private static partial Regex OperatorsCi();
    [GeneratedRegex("^(AND|OR|NOT)$", RegexOptions.IgnoreCase)] private static partial Regex OperatorWord();
    [GeneratedRegex(@"\b(OR|NOT)\b", RegexOptions.IgnoreCase)] private static partial Regex OrNot();
    [GeneratedRegex(@"\bAND\b", RegexOptions.IgnoreCase)] private static partial Regex AndWord();
    [GeneratedRegex(@"[^A-Za-z0-9_\-]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"[^A-Za-z0-9_\-\s]+")] private static partial Regex NonWordKeepSpace();
}
